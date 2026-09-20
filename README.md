# Vexta CPU Miner

Official CPU miner for **Vexta RandomX**.

Vexta CPU Miner is a standalone RandomX miner designed specifically for the Vexta network.

## Download

Latest release:

https://github.com/VextaProject/Vexta-CPU-Miner/releases/latest

Prebuilt binaries are available for:

- Windows x64
- Linux x64

## Features

- Vexta RandomX mining
- RandomX full-memory mode
- Windows x64 and Linux x64 support
- Configurable CPU thread count
- Local hashrate display
- Accepted / rejected share statistics
- Automatic RandomX seed changes
- Daemon-confirmed `BLOCK FOUND` notifications when supported by the pool

## Usage

### Windows

```text
vexta-cpuminer.exe -o stratum+tcp://POOL:PORT -u WALLET.WORKER -p x -t THREADS
```

Example:

```text
vexta-cpuminer.exe -o stratum+tcp://pool.example.com:9335 -u YOUR_VEXTA_ADDRESS.worker1 -p x -t 4
```

### Linux

```text
./vexta-cpuminer -o stratum+tcp://POOL:PORT -u WALLET.WORKER -p x -t THREADS
```

Example:

```text
./vexta-cpuminer -o stratum+tcp://pool.example.com:9335 -u YOUR_VEXTA_ADDRESS.worker1 -p x -t 4
```

## Parameters

| Parameter | Description |
|---|---|
| `-o` | Stratum pool address |
| `-u` | Vexta wallet address, optionally followed by `.worker` |
| `-p` | Pool password, normally `x` |
| `-t` | Number of CPU mining threads |

## Requirements

- 64-bit Windows or Linux
- Approximately 2+ GB available RAM for the RandomX dataset
- AES-capable CPU recommended

RandomX full-memory mode is enabled.

## Building from Source

### Requirements

- .NET 6 SDK
- Git
- CMake
- C/C++ build tools
- RandomX v2.0.1

Clone the miner:

```bash
git clone https://github.com/VextaProject/Vexta-CPU-Miner.git
cd Vexta-CPU-Miner
```

### Linux x64

```bash
dotnet restore

dotnet publish Vexta.CpuMiner.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true
```

### Windows x64

```bash
dotnet restore

dotnet publish Vexta.CpuMiner.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true
```

## RandomX Native Library

The miner uses the official RandomX implementation:

https://github.com/tevador/RandomX

Tested version: **v2.0.1**

### Linux example

```bash
git clone --branch v2.0.1 https://github.com/tevador/RandomX.git
cd RandomX

cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j$(nproc)
```

Place `librandomx.so` next to `vexta-cpuminer`.

### Windows

Build RandomX as a 64-bit DLL and place `librandomx.dll` next to `vexta-cpuminer.exe`.

The official release packages already include the required native RandomX library.

## Pool Compatibility

This miner is specifically designed for **Vexta RandomX**.

It uses a Bitcoin-style 80-byte block header.

Supported Stratum methods:

- `mining.subscribe`
- `mining.authorize`
- `mining.set_difficulty`
- `mining.notify`
- `mining.submit`

### RandomX Seed

Vexta RandomX pools must provide the RandomX seed as the **10th parameter** of `mining.notify`.

The miner uses this seed to initialise and update the RandomX dataset.

### BLOCK FOUND Notification

The miner supports the optional Vexta Stratum extension:

```text
mining.block_found
```

Example:

```json
{
  "id": null,
  "method": "mining.block_found",
  "params": [
    12345,
    "BLOCK_HASH"
  ]
}
```

This notification should only be sent after the daemon has accepted the block.

Pools that do not implement `mining.block_found` can still be used normally. The miner will simply not display the confirmed `BLOCK FOUND` message.

## License

MIT License

Copyright (c) 2026 Vexta Project
