using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Vexta.CpuMiner;

public static unsafe class VextaRandomX
{
    [Flags]
    public enum RandomXFlags
    {
        Default = 0,
        LargePages = 1,
        HardAes = 2,
        FullMem = 4,
        Jit = 8,
        Secure = 16,
        Argon2Ssse3 = 32,
        Argon2Avx2 = 64,
        Argon2 = 96
    }

    [DllImport("librandomx", EntryPoint = "randomx_get_flags", CallingConvention = CallingConvention.Cdecl)]
    private static extern RandomXFlags GetFlags();

    [DllImport("librandomx", EntryPoint = "randomx_alloc_cache", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr AllocCache(RandomXFlags flags);

    [DllImport("librandomx", EntryPoint = "randomx_init_cache", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr InitCache(IntPtr cache, IntPtr key, int keySize);

    [DllImport("librandomx", EntryPoint = "randomx_release_cache", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ReleaseCache(IntPtr cache);

    [DllImport("librandomx", EntryPoint = "randomx_alloc_dataset", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr AllocDataset(RandomXFlags flags);

    [DllImport("librandomx", EntryPoint = "randomx_dataset_item_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong DatasetItemCount();

    [DllImport("librandomx", EntryPoint = "randomx_init_dataset", CallingConvention = CallingConvention.Cdecl)]
    private static extern void InitDataset(
        IntPtr dataset,
        IntPtr cache,
        ulong startItem,
        ulong itemCount);

    [DllImport("librandomx", EntryPoint = "randomx_release_dataset", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ReleaseDataset(IntPtr dataset);

    [DllImport("librandomx", EntryPoint = "randomx_create_vm", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CreateVm(
        RandomXFlags flags,
        IntPtr cache,
        IntPtr dataset);

    [DllImport("librandomx", EntryPoint = "randomx_destroy_vm", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroyVm(IntPtr machine);

    [DllImport("librandomx", EntryPoint = "randomx_calculate_hash", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CalculateHashNative(
        IntPtr machine,
        byte* input,
        int inputSize,
        byte* output);

    private sealed class GenContext : IDisposable
    {
        public int VmCount { get; init; }

        public IntPtr Cache { get; private set; }

        public IntPtr Dataset { get; private set; }

        public void Init(ReadOnlySpan<byte> key, RandomXFlags flags)
        {
            Cache = AllocCache(flags);

            if(Cache == IntPtr.Zero)
                throw new InvalidOperationException("Unable to allocate RandomX cache.");

            fixed(byte* keyPtr = key)
            {
                InitCache(Cache, (IntPtr)keyPtr, key.Length);
            }

            if((flags & RandomXFlags.FullMem) != 0)
            {
                Dataset = AllocDataset(flags);

                if(Dataset == IntPtr.Zero)
                    throw new InvalidOperationException("Unable to allocate RandomX dataset.");

                var itemCount = DatasetItemCount();
                InitDataset(Dataset, Cache, 0, itemCount);

                ReleaseCache(Cache);
                Cache = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if(Dataset != IntPtr.Zero)
            {
                ReleaseDataset(Dataset);
                Dataset = IntPtr.Zero;
            }

            if(Cache != IntPtr.Zero)
            {
                ReleaseCache(Cache);
                Cache = IntPtr.Zero;
            }
        }
    }

    private sealed class RxVm : IDisposable
    {
        private IntPtr vm;

        public void Init(
            RandomXFlags flags,
            IntPtr cache,
            IntPtr dataset)
        {
            vm = CreateVm(flags, cache, dataset);

            if(vm == IntPtr.Zero)
                throw new InvalidOperationException("Unable to create RandomX VM.");
        }

        public void CalculateHash(
            ReadOnlySpan<byte> data,
            Span<byte> result)
        {
            fixed(byte* input = data)
            fixed(byte* output = result)
            {
                CalculateHashNative(vm, input, data.Length, output);
            }
        }

        public void Dispose()
        {
            if(vm != IntPtr.Zero)
            {
                DestroyVm(vm);
                vm = IntPtr.Zero;
            }
        }
    }

    private sealed class SeedContext
    {
        public GenContext Context { get; init; } = null!;

        public BlockingCollection<RxVm> Vms { get; init; } = null!;
    }

    private static readonly Dictionary<string, Dictionary<string, SeedContext>> Realms = new();

    public static void CreateSeed(
        string realm,
        string seedHex,
        int vmCount = 1)
    {
        lock(Realms)
        {
            if(!Realms.TryGetValue(realm, out var seeds))
            {
                seeds = new Dictionary<string, SeedContext>();
                Realms[realm] = seeds;
            }

            if(seeds.ContainsKey(seedHex))
                return;

            if(vmCount == -1)
                vmCount = Environment.ProcessorCount;

            var flags = GetFlags() | RandomXFlags.FullMem;

            var context = new GenContext
            {
                VmCount = vmCount
            };

            context.Init(Convert.FromHexString(seedHex), flags);

            var vms = new BlockingCollection<RxVm>();

            Parallel.For(0, vmCount, _ =>
            {
                var vm = new RxVm();
                vm.Init(flags, context.Cache, context.Dataset);
                vms.Add(vm);
            });

            seeds[seedHex] = new SeedContext
            {
                Context = context,
                Vms = vms
            };
        }
    }

    public static void DeleteSeed(string realm, string seedHex)
    {
        SeedContext? seed;

        lock(Realms)
        {
            if(!Realms.TryGetValue(realm, out var seeds))
                return;

            if(!seeds.Remove(seedHex, out seed))
                return;
        }

        for(var i = 0; i < seed.Context.VmCount; i++)
        {
            var vm = seed.Vms.Take();
            vm.Dispose();
        }

        seed.Context.Dispose();
        seed.Vms.Dispose();
    }

    public static void CalculateHash(
        string realm,
        string seedHex,
        ReadOnlySpan<byte> data,
        Span<byte> result)
    {
        if(result.Length < 32)
            throw new ArgumentException(
                "RandomX result buffer must be at least 32 bytes.",
                nameof(result));

        SeedContext? seed = null;

        lock(Realms)
        {
            if(Realms.TryGetValue(realm, out var seeds))
                seeds.TryGetValue(seedHex, out seed);
        }

        if(seed is null)
        {
            result[..32].Clear();
            return;
        }

        RxVm? vm = null;

        try
        {
            vm = seed.Vms.Take();
            vm.CalculateHash(data, result);
        }
        finally
        {
            if(vm is not null)
                seed.Vms.Add(vm);
        }
    }
}
