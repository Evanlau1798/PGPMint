# PGPMint Metal bridge

Requires macOS 11+, Xcode Command Line Tools and the macOS SDK. From the repository root:

```sh
sh src/PGPMint.Core/Native/build-metal.sh /absolute/path/to/publish
```

The script builds a universal arm64/x86_64 `libpgpmint_metal.dylib`. Package it beside
the managed executable/Core assembly. The enclosing application must include the
dylib in its normal signing/notarization process. No NuGet Metal binding is needed.
The Core project builds the bridge automatically on macOS and copies it into
dependent application build and publish outputs.

`MetalBackend` enumerates Metal devices by full 64-bit registry ID. Its stable
`DeviceKey` is `metal:` followed by 16 lowercase hex digits. Unified memory is not
reported as dedicated VRAM. The driver identity is the macOS version, since Metal
drivers ship with the OS. The actual pipeline threadgroup size is recorded when
creating a device.

`new MetalBackend(cacheDirectory: null, workgroupSize: 64)` accepts requested
workgroup sizes including 64, 128 (default) and 256. Native creation clamps to the
pipeline limit and rounds down to an execution-width multiple where possible;
`Info.KernelConfiguration.WorkgroupSize` records the actual size. The optional
cache directory is accepted for factory compatibility; Metal owns compiler caching.

Each device owns a compiled four-lane pipeline, queue, schedule/parameter buffers
and a result buffer that grows as needed. Dispatch is synchronous and completes
the entire timestamp range even in FindOne mode; the atomic slot only bounds
returned hits. `GpuSearchBatch` owns overflow rescans, cancellation between
dispatches, unique-key counts and complete-range/evaluation statistics. CPU
fingerprint validation remains in the existing coordinator. The C ABI accepts
only public SHA-1 schedules and search parameters, never private seeds.

The managed device serializes scans and disposal. The C ABI serializes dispatches
within a session; callers must not race destruction against another call. Native
errors are copied into caller-owned UTF-8 buffers. GPU command failures return
`-2` and become `GpuDeviceLostException`; other failures return `-1`. A SafeHandle
owns native cleanup, including forgotten managed disposal.

Windows managed tests use an injected session to verify host-side search behavior
against CPU SHA-1. They do not compile Objective-C++ or run the MSL on a GPU.
Before macOS release, build/sign the bridge and run the explicit Metal hardware
tests on both available architecture/device families; check dense overflow,
FindOne, zero hits, timestamp boundaries, repeated dispatches and cancellation.
