using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.DataEncoders;
using Vexta.CpuMiner;

internal static class Program
{
    private static readonly object ConsoleLock = new();
    private static readonly BigInteger Diff1 =
        BigInteger.Parse(
            "00ffff0000000000000000000000000000000000000000000000000000",
            NumberStyles.HexNumber);

    private const string Realm = "vexta-cpuminer";

    private static readonly SemaphoreSlim WriterLock = new(1, 1);
    private static readonly SemaphoreSlim JobLock = new(1, 1);

    private static readonly ConcurrentDictionary<long, string> PendingSubmits = new();

    private static StreamWriter? writer;

    private static string worker = "";
    private static string password = "x";
    private static string extraNonce1 = "";
    private static int extraNonce2Size;
    private static int threadCount;

    private static double currentDifficulty;

    private static CancellationTokenSource? currentJobCts;
    private static Task? currentMiningTask;
    private static string? currentSeed;

    private static long nextSubmitId = 100;
    private static long totalHashes;
    private static long acceptedShares;
    private static long rejectedShares;

    private static async Task Main(string[] args)
    {
        try
        {
            await RunAsync(args);
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunAsync(string[] args)
    {
        if(args.Length == 0 || Array.Exists(args, x => x == "-h" || x == "--help"))
        {
            PrintHelp();
            return;
        }

        var poolUrl = GetArg(args, "-o")
            ?? throw new ArgumentException("Missing required option: -o");

        worker = GetArg(args, "-u")
            ?? throw new ArgumentException("Missing required option: -u");

        password = GetArg(args, "-p") ?? "x";

        var threadText = GetArg(args, "-t");

        threadCount = threadText != null
            ? int.Parse(threadText, CultureInfo.InvariantCulture)
            : Environment.ProcessorCount;

        if(threadCount < 1)
            throw new ArgumentException("Thread count must be at least 1");

        if(!poolUrl.StartsWith("stratum+tcp://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pool URL must start with stratum+tcp://");

        var endpoint = poolUrl["stratum+tcp://".Length..];
        var colon = endpoint.LastIndexOf(':');

        if(colon <= 0 ||
           !int.TryParse(endpoint[(colon + 1)..], out var port) ||
           port < 1 ||
           port > 65535)
        {
            throw new ArgumentException(
                "Invalid Stratum URL. Expected stratum+tcp://HOST:PORT");
        }

        var host = endpoint[..colon];

        using var shutdown = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;

            if(!shutdown.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("Stopping miner...");
                shutdown.Cancel();
            }
        };

        PrintBanner();
        Console.WriteLine($"Pool    : {host}:{port}");
        Console.WriteLine($"Worker  : {worker}");
        Console.WriteLine($"Threads : {threadCount}");
        Console.WriteLine();

        var statsTask = StatsLoopAsync(shutdown.Token);

        while(!shutdown.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(host, port, shutdown.Token);
            }
            catch(OperationCanceledException) when(shutdown.IsCancellationRequested)
            {
                break;
            }
            catch(Exception ex)
            {
                if(!shutdown.IsCancellationRequested)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Disconnected: {ex.Message}");
                }

                await StopCurrentJobAsync();

                if(shutdown.IsCancellationRequested)
                    break;

                Console.WriteLine("Reconnecting in 5 seconds...");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), shutdown.Token);
                }
                catch(OperationCanceledException)
                {
                    break;
                }
            }
        }

        await StopCurrentJobAsync();

        try
        {
            await statsTask;
        }
        catch(OperationCanceledException)
        {
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Stopped. Accepted: {Interlocked.Read(ref acceptedShares)}, " +
            $"Rejected: {Interlocked.Read(ref rejectedShares)}");
    }

    private static async Task RunSessionAsync(
        string host,
        int port,
        CancellationToken shutdown)
    {
        Console.WriteLine($"Connecting to {host}:{port} ...");

        using var tcp = new TcpClient();

        await tcp.ConnectAsync(host, port, shutdown);

        // ReadLineAsync() in .NET 6 cannot take a CancellationToken.
        // Closing the socket on shutdown unblocks any pending Stratum read.
        using var shutdownRegistration = shutdown.Register(() =>
        {
            try
            {
                tcp.Dispose();
            }
            catch
            {
            }
        });

        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);

        writer = new StreamWriter(stream, Encoding.ASCII)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        extraNonce1 = "";
        extraNonce2Size = 0;
        currentDifficulty = 0;

        await SendLineAsync(
            "{\"id\":1,\"method\":\"mining.subscribe\",\"params\":[\"VextaCpuMiner/0.1\"]}",
            shutdown);

        JsonElement? firstNotify = null;
        var subscribed = false;
        var authorized = false;
        var authorizeSent = false;

        while(!shutdown.IsCancellationRequested &&
              (!subscribed || !authorized || firstNotify == null || currentDifficulty <= 0))
        {
            var line = await reader.ReadLineAsync()
                ?? throw new IOException("Stratum connection closed");

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if(root.TryGetProperty("id", out var id) &&
               id.ValueKind == JsonValueKind.Number)
            {
                var value = id.GetInt64();

                if(value == 1)
                {
                    var result = root.GetProperty("result");

                    extraNonce1 = result[1].GetString()
                        ?? throw new Exception("Pool returned invalid ExtraNonce1");

                    extraNonce2Size = result[2].GetInt32();
                    subscribed = true;

                    Console.WriteLine(
                        $"Subscribed. ExtraNonce1={extraNonce1}, " +
                        $"ExtraNonce2Size={extraNonce2Size}");

                    if(!authorizeSent)
                    {
                        await SendAuthorizeAsync(shutdown);
                        authorizeSent = true;
                    }

                    continue;
                }

                if(value == 2)
                {
                    if(root.TryGetProperty("result", out var result) &&
                       result.ValueKind == JsonValueKind.True)
                    {
                        authorized = true;
                        WriteColor("Authorized.", ConsoleColor.Green);
                    }
                    else
                    {
                        throw new Exception("Pool authorization failed");
                    }

                    continue;
                }
            }

            if(root.TryGetProperty("method", out var method) &&
               method.ValueKind == JsonValueKind.String)
            {
                var methodName = method.GetString();

                if(methodName == "mining.block_found")
                {
                    var parameters = root.GetProperty("params");

                    var height = parameters[0].GetInt64();
                    var hash = parameters[1].GetString() ?? "";

                    WriteColor(
                        $"*** BLOCK FOUND! Height {height} ***",
                        ConsoleColor.Magenta);

                    WriteColor(
                        $"Hash: {hash}",
                        ConsoleColor.Magenta);

                    continue;
                }

                if(methodName == "mining.set_difficulty")
                {
                    currentDifficulty = root
                        .GetProperty("params")[0]
                        .GetDouble();

                    Console.WriteLine(
                        $"Difficulty set to {currentDifficulty.ToString("G", CultureInfo.InvariantCulture)}");

                    continue;
                }

                if(methodName == "mining.notify")
                {
                    firstNotify = root.GetProperty("params").Clone();
                    continue;
                }
            }
        }

        if(firstNotify == null)
            throw new Exception("Pool did not provide an initial mining job");

        await SwitchJobAsync(firstNotify.Value, shutdown);

        Console.WriteLine("Mining started.");
        Console.WriteLine();

        while(!shutdown.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync()
                ?? throw new IOException("Stratum connection closed");

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if(root.TryGetProperty("method", out var method) &&
               method.ValueKind == JsonValueKind.String)
            {
                var methodName = method.GetString();

                if(methodName == "mining.block_found")
                {
                    var parameters = root.GetProperty("params");

                    var height = parameters[0].GetInt64();
                    var hash = parameters[1].GetString() ?? "";

                    WriteColor(
                        $"*** BLOCK FOUND! Height {height} ***",
                        ConsoleColor.Magenta);

                    WriteColor(
                        $"Hash: {hash}",
                        ConsoleColor.Magenta);

                    continue;
                }

                if(methodName == "mining.set_difficulty")
                {
                    var diff = root
                        .GetProperty("params")[0]
                        .GetDouble();

                    if(diff > 0)
                    {
                        currentDifficulty = diff;

                        Console.WriteLine(
                            $"Difficulty changed to {diff.ToString("G", CultureInfo.InvariantCulture)}");
                    }

                    continue;
                }

                if(methodName == "mining.notify")
                {
                    var job = root.GetProperty("params").Clone();

                    await SwitchJobAsync(job, shutdown);
                    continue;
                }

                continue;
            }

            if(root.TryGetProperty("id", out var id) &&
               id.ValueKind == JsonValueKind.Number)
            {
                var submitId = id.GetInt64();

                if(PendingSubmits.TryRemove(submitId, out var submittedJob))
                {
                    var accepted =
                        root.TryGetProperty("result", out var result) &&
                        result.ValueKind == JsonValueKind.True;

                    if(accepted)
                    {
                        var count = Interlocked.Increment(ref acceptedShares);
                        WriteColor(
                            $"ACCEPTED share #{count}  job={submittedJob}",
                            ConsoleColor.Green);
                    }
                    else
                    {
                        var count = Interlocked.Increment(ref rejectedShares);

                        string errorText = "";

                        if(root.TryGetProperty("error", out var error) &&
                           error.ValueKind != JsonValueKind.Null)
                        {
                            errorText = $"  error={error}";
                        }

                        WriteColor(
                            $"REJECTED share #{count}  job={submittedJob}{errorText}",
                            ConsoleColor.Red);
                    }
                }
            }
        }
    }

    private static async Task SendAuthorizeAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            id = 2,
            method = "mining.authorize",
            @params = new object[] { worker, password }
        });

        await SendLineAsync(json, ct);
    }

    private static async Task SendLineAsync(
        string line,
        CancellationToken ct)
    {
        var target = writer
            ?? throw new IOException("Stratum writer is not connected");

        await WriterLock.WaitAsync(ct);

        try
        {
            await target.WriteLineAsync(line);
            await target.FlushAsync();
        }
        finally
        {
            WriterLock.Release();
        }
    }

    private static async Task SwitchJobAsync(
        JsonElement job,
        CancellationToken shutdown)
    {
        await JobLock.WaitAsync(shutdown);

        try
        {
            if(job.ValueKind != JsonValueKind.Array || job.GetArrayLength() < 10)
            {
                throw new Exception(
                    "RandomX Stratum job does not contain the 10th randomx_seed parameter");
            }

            var parsed = ParseJob(job);

            await StopCurrentJobAsync();

            if(currentSeed != null &&
               !currentSeed.Equals(parsed.Seed, StringComparison.OrdinalIgnoreCase))
            {
                VextaRandomX.DeleteSeed(Realm, currentSeed);
                currentSeed = null;
            }

            if(currentSeed == null)
            {
                WriteColor(
                    "Preparing RandomX dataset...",
                    ConsoleColor.Cyan);

                Console.WriteLine($"Seed: {parsed.Seed}");

                VextaRandomX.CreateSeed(
                    Realm,
                    parsed.Seed,
                    vmCount: threadCount);

                currentSeed = parsed.Seed;

                WriteColor(
                    "RandomX dataset ready.",
                    ConsoleColor.Green);
            }

            currentJobCts =
                CancellationTokenSource.CreateLinkedTokenSource(shutdown);

            var token = currentJobCts.Token;

            WriteColor(
                $"NEW JOB {parsed.JobId}  nTime={parsed.NTimeHex}  seed={parsed.Seed}",
                ConsoleColor.Yellow);

            currentMiningTask =
                MineJobAsync(parsed, token);
        }
        finally
        {
            JobLock.Release();
        }
    }

    private static async Task StopCurrentJobAsync()
    {
        var cts = currentJobCts;
        var task = currentMiningTask;

        currentJobCts = null;
        currentMiningTask = null;

        if(cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }
        }

        if(task != null)
        {
            try
            {
                await task;
            }
            catch(OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
    }

    private static MiningJob ParseJob(JsonElement job)
    {
        var jobId = job[0].GetString()
            ?? throw new Exception("Invalid job id");

        var prevHashLE = job[1].GetString()
            ?? throw new Exception("Invalid previous block hash");

        var coinbase1 = job[2].GetString()
            ?? throw new Exception("Invalid coinbase1");

        var coinbase2 = job[3].GetString()
            ?? throw new Exception("Invalid coinbase2");

        var branches = job[4].Clone();

        var versionHex = job[5].GetString()
            ?? throw new Exception("Invalid version");

        var bitsHex = job[6].GetString()
            ?? throw new Exception("Invalid bits");

        var nTimeHex = job[7].GetString()
            ?? throw new Exception("Invalid nTime");

        var seed = job[9].GetString()
            ?? throw new Exception("Invalid RandomX seed");

        var extraNonce2 = new string('0', extraNonce2Size * 2);

        var coinbaseHex =
            coinbase1 +
            extraNonce1 +
            extraNonce2 +
            coinbase2;

        var coinbase = Convert.FromHexString(coinbaseHex);
        var coinbaseHash = Sha256D(coinbase);
        var merkleRoot = BuildMerkleRoot(coinbaseHash, branches);

        var prevBytes = Convert.FromHexString(prevHashLE);

        // Miningcore sends previousBlockHash using ReverseByteOrder():
        // reverse the order of eight 4-byte words, not all 32 bytes.
        var restoredPrevBytes = new byte[prevBytes.Length];

        for(var i = 0; i < prevBytes.Length; i += 4)
        {
            Array.Copy(
                prevBytes,
                prevBytes.Length - 4 - i,
                restoredPrevBytes,
                i,
                4);
        }

        var prevDisplay =
            Convert.ToHexString(restoredPrevBytes).ToLowerInvariant();

        var version = uint.Parse(
            versionHex,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);

        var nTime = uint.Parse(
            nTimeHex,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);

        return new MiningJob(
            jobId,
            prevDisplay,
            merkleRoot,
            version,
            bitsHex,
            nTime,
            nTimeHex,
            seed,
            extraNonce2);
    }

    private static async Task MineJobAsync(
        MiningJob job,
        CancellationToken ct)
    {
        var tasks = new Task[threadCount];

        for(var workerIndex = 0; workerIndex < threadCount; workerIndex++)
        {
            var localIndex = workerIndex;

            tasks[workerIndex] = Task.Run(
                async () =>
                {
                    for(ulong n = (ulong)localIndex;
                        n <= uint.MaxValue && !ct.IsCancellationRequested;
                        n += (ulong)threadCount)
                    {
                        var nonce = (uint)n;

#pragma warning disable 618
                        var header = new BlockHeader
#pragma warning restore 618
                        {
                            Version = unchecked((int)job.Version),
                            HashPrevBlock = uint256.Parse(job.PrevDisplay),
                            HashMerkleRoot = new uint256(job.MerkleRoot),
                            BlockTime = DateTimeOffset.FromUnixTimeSeconds(job.NTime),
                            Bits = new Target(
                                Encoders.Hex.DecodeData(job.BitsHex)),
                            Nonce = nonce
                        };

                        var headerBytes = header.ToBytes();
                        var rxHash = new byte[32];

                        VextaRandomX.CalculateHash(
                            Realm,
                            job.Seed,
                            headerBytes,
                            rxHash);

                        Interlocked.Increment(ref totalHashes);

                        var hashValue = new BigInteger(
                            rxHash,
                            isUnsigned: true,
                            isBigEndian: false);

                        if(hashValue.IsZero)
                            continue;

                        var shareDifficulty =
                            (double)Diff1 /
                            (double)hashValue;

                        var required =
                            Volatile.Read(ref currentDifficulty) * 0.99d;

                        if(required <= 0 ||
                           shareDifficulty < required)
                        {
                            continue;
                        }

                        var nonceHex =
                            nonce.ToString("x8", CultureInfo.InvariantCulture);

                        Console.WriteLine(
                            $"FOUND share  job={job.JobId} nonce={nonceHex} diff={shareDifficulty:E6}");

                        try
                        {
                            await SubmitShareAsync(
                                job,
                                nonceHex,
                                ct);
                        }
                        catch(OperationCanceledException)
                        {
                            break;
                        }
                        catch(Exception ex)
                        {
                            Console.WriteLine(
                                $"Submit failed: {ex.Message}");
                        }
                    }
                },
                ct);
        }

        await Task.WhenAll(tasks);
    }

    private static async Task SubmitShareAsync(
        MiningJob job,
        string nonceHex,
        CancellationToken ct)
    {
        var submitId =
            Interlocked.Increment(ref nextSubmitId);

        PendingSubmits[submitId] = job.JobId;

        var submit = JsonSerializer.Serialize(new
        {
            id = submitId,
            method = "mining.submit",
            @params = new object[]
            {
                worker,
                job.JobId,
                job.ExtraNonce2,
                job.NTimeHex,
                nonceHex
            }
        });

        try
        {
            await SendLineAsync(submit, ct);
        }
        catch
        {
            PendingSubmits.TryRemove(submitId, out _);
            throw;
        }
    }

    private static async Task StatsLoopAsync(CancellationToken ct)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();

        var lastHashes = Interlocked.Read(ref totalHashes);
        var lastTime = sw.Elapsed;
        var measuring = false;

        try
        {
            while(await timer.WaitForNextTickAsync(ct))
            {
                var now = sw.Elapsed;
                var hashes = Interlocked.Read(ref totalHashes);

                if(hashes == 0)
                {
                    lastHashes = hashes;
                    lastTime = now;
                    continue;
                }

                if(measuring && hashes == lastHashes)
                {
                    lastTime = now;
                    measuring = false;
                    continue;
                }

                if(!measuring)
                {
                    lastHashes = hashes;
                    lastTime = now;
                    measuring = true;
                    continue;
                }

                var deltaHashes = hashes - lastHashes;
                var deltaTime = (now - lastTime).TotalSeconds;

                var rate =
                    deltaTime > 0
                        ? deltaHashes / deltaTime
                        : 0;

                WriteColor(
                    $"HASHRATE {FormatHashrate(rate)}  " +
                    $"A:{Interlocked.Read(ref acceptedShares)} " +
                    $"R:{Interlocked.Read(ref rejectedShares)}",
                    ConsoleColor.Cyan);

                lastHashes = hashes;
                lastTime = now;
            }
        }
        finally
        {
            timer.Dispose();
        }
    }

    private static string FormatHashrate(double value)
    {
        if(value >= 1_000_000)
            return $"{value / 1_000_000:F2} MH/s";

        if(value >= 1_000)
            return $"{value / 1_000:F2} kH/s";

        return $"{value:F2} H/s";
    }

    private static byte[] Sha256D(byte[] data)
    {
        using var sha = SHA256.Create();

        var first = sha.ComputeHash(data);
        return sha.ComputeHash(first);
    }

    private static byte[] BuildMerkleRoot(
        byte[] coinbaseHash,
        JsonElement branches)
    {
        var current = coinbaseHash;

        foreach(var branch in branches.EnumerateArray())
        {
            var branchBytes =
                Convert.FromHexString(branch.GetString()!);

            var combined =
                new byte[current.Length + branchBytes.Length];

            Buffer.BlockCopy(
                current, 0,
                combined, 0,
                current.Length);

            Buffer.BlockCopy(
                branchBytes, 0,
                combined, current.Length,
                branchBytes.Length);

            current = Sha256D(combined);
        }

        return current;
    }

    private static string? GetArg(
        string[] args,
        string name)
    {
        for(var i = 0; i < args.Length - 1; i++)
        {
            if(args[i].Equals(
                name,
                StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void PrintBanner()
    {
        var oldColor = Console.ForegroundColor;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"*   *  *****  *   *  *****   ***
*   *  *       * *     *    *   *
*   *  ****     *      *    *****
 * *   *       * *     *    *   *
  *    *****  *   *    *    *   *");

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine();
        Console.WriteLine("Vexta CPU Miner 0.1.0");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Copyright (c) 2026 Vexta Project");
        Console.WriteLine("Licensed under the MIT License");

        Console.ForegroundColor = oldColor;
        Console.WriteLine();
    }

    private static void WriteColor(string text, ConsoleColor color)
    {
        lock(ConsoleLock)
        {
            var oldColor = Console.ForegroundColor;

            try
            {
                Console.ForegroundColor = color;
                Console.WriteLine(text);
            }
            finally
            {
                Console.ForegroundColor = oldColor;
            }
        }
    }

    private static void PrintHelp()
    {
        PrintBanner();
        Console.WriteLine("Usage:");
        Console.WriteLine(
            "  Vexta.CpuMiner -o stratum+tcp://HOST:PORT -u ADDRESS.worker [-p x] [-t THREADS]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -o   Stratum pool URL");
        Console.WriteLine("  -u   Vexta address / worker name");
        Console.WriteLine("  -p   Password (default: x)");
        Console.WriteLine(
            "  -t   CPU threads (default: logical CPU count)");
        Console.WriteLine();
        Console.WriteLine("Ctrl+C stops the miner cleanly.");
    }

    private sealed record MiningJob(
        string JobId,
        string PrevDisplay,
        byte[] MerkleRoot,
        uint Version,
        string BitsHex,
        uint NTime,
        string NTimeHex,
        string Seed,
        string ExtraNonce2);
}
