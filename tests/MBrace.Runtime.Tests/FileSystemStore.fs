﻿namespace MBrace.Runtime.Tests

open NUnit.Framework

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Core.Tests
open MBrace.CSharp.Tests

open MBrace.Runtime
open MBrace.Runtime.Utils.XPlat
open MBrace.Runtime.Components
open MBrace.ThreadPool

[<TestFixture>]
type ``Local FileSystemStore Tests`` () =
    inherit ``CloudFileStore Tests``(parallelismFactor = 100)

    let fsStore = FileSystemStore.CreateRandomLocal()
    let serializer = new FsPicklerBinarySerializer(useVagabond = false)
    let imem = ThreadPoolRuntime.Create(fileStore = fsStore, serializer = serializer, memoryEmulation = MemoryEmulation.Copied)

    override __.FileStore = fsStore :> _
    override __.Serializer = serializer :> _
    override __.IsCaseSensitive =
        match currentPlatform.Value with
        | Platform.BSD | Platform.Unix | Platform.Linux -> true
        | _ -> false

    override __.Run(wf : Cloud<'T>) = imem.RunSynchronously wf
    override __.RunLocally(wf : Cloud<'T>) = imem.RunSynchronously wf


[<TestFixture>]
type ``Local FileSystemStore CloudValue Tests`` () =
    inherit ``CloudValue Tests``(parallelismFactor = 100)

    // StoreCloudValueProvider depends on Vagabond, ensure enabled
    do VagabondRegistry.Initialize(isClientSession = true)
    let fsStore = FileSystemStore.CreateRandomLocal()
    let serializer = new FsPicklerBinarySerializer(useVagabond = false)
    let cloudValueProvider = StoreCloudValueProvider.InitCloudValueProvider(fsStore, serializer = serializer, encapsulationThreshold = 1024L) :> ICloudValueProvider
    let imem = ThreadPoolRuntime.Create(fileStore = fsStore, serializer = serializer, valueProvider = cloudValueProvider, memoryEmulation = MemoryEmulation.Copied)

    override __.Run(wf : Cloud<'T>) = imem.RunSynchronously wf
    override __.RunLocally(wf : Cloud<'T>) = imem.RunSynchronously wf
    override __.IsSupportedLevel lvl = cloudValueProvider.IsSupportedStorageLevel lvl


[<TestFixture>]
type ``Local FileSystemStore CloudFlow Tests`` () =
    inherit ``CloudFlow tests``()
    
    // StoreCloudValueProvider depends on Vagabond, ensure enabled
    do VagabondRegistry.Initialize(isClientSession = true)
    let fsStore = FileSystemStore.CreateRandomLocal()
    let serializer = new FsPicklerBinarySerializer(useVagabond = false)
    let cloudValueProvider = StoreCloudValueProvider.InitCloudValueProvider(mainStore = fsStore, serializer = serializer, encapsulationThreshold = 1024L) :> ICloudValueProvider
    let imem = ThreadPoolRuntime.Create(fileStore = fsStore, serializer = serializer, valueProvider = cloudValueProvider, memoryEmulation = MemoryEmulation.Copied)

    override __.Run(wf : Cloud<'T>) = imem.RunSynchronously wf
    override __.RunLocally(wf : Cloud<'T>) = imem.RunSynchronously wf
    override __.RunWithLogs(workflow : Cloud<unit>) =
        let logTester = new InMemoryLogTester()
        let imem = ThreadPoolRuntime.Create(fileStore = fsStore, serializer = serializer, logger = logTester, memoryEmulation = MemoryEmulation.Copied)
        imem.RunSynchronously workflow
        logTester.GetLogs()

    override __.IsSupportedStorageLevel level = cloudValueProvider.IsSupportedStorageLevel level
    override __.FsCheckMaxNumberOfTests = if isCIInstance then 20 else 100
    override __.FsCheckMaxNumberOfIOBoundTests = if isCIInstance then 5 else 30


[<TestFixture>]
type ``Local FileSystemStore CSharp Tests`` () =
    inherit ``CloudTests``()

    // StoreCloudValueProvider depends on Vagabond, ensure enabled
    do VagabondRegistry.Initialize(isClientSession = true)
    let fsStore = FileSystemStore.CreateRandomLocal()
    let serializer = new FsPicklerBinarySerializer(useVagabond = false)
    let cloudValueProvider = StoreCloudValueProvider.InitCloudValueProvider(mainStore = fsStore, serializer = serializer, encapsulationThreshold = 1024L) :> ICloudValueProvider
    let imem = ThreadPoolRuntime.Create(fileStore = fsStore, serializer = serializer, valueProvider = cloudValueProvider, memoryEmulation = MemoryEmulation.Copied)

    override __.Run(wf : Cloud<'T>) = imem.RunSynchronously wf
    override __.RunLocally(wf : Cloud<'T>) = imem.RunSynchronously wf
    override __.RunWithLogs(workflow : Cloud<unit>) =
        let logTester = new InMemoryLogTester()
        let imem = ThreadPoolRuntime.Create(fileStore = fsStore, serializer = serializer, logger = logTester, memoryEmulation = MemoryEmulation.Copied)
        imem.RunSynchronously workflow
        logTester.GetLogs()

[<TestFixture>]
type ``Local FileSystemStore CSharp CloudFlow Tests`` () =
    inherit CloudFlowTests()

    // StoreCloudValueProvider depends on Vagabond, ensure enabled
    do VagabondRegistry.Initialize(isClientSession = true)
    let fsStore = FileSystemStore.CreateRandomLocal()
    let serializer = new FsPicklerBinarySerializer(useVagabond = false)
    let cloudValueProvider = StoreCloudValueProvider.InitCloudValueProvider(mainStore = fsStore, serializer = serializer, encapsulationThreshold = 1024L) :> ICloudValueProvider
    let imem = ThreadPoolRuntime.Create(fileStore = fsStore, serializer = serializer, valueProvider = cloudValueProvider, memoryEmulation = MemoryEmulation.Copied)

    override __.Run(wf : Cloud<'T>) = imem.RunSynchronously wf
    override __.RunLocally(wf : Cloud<'T>) = imem.RunSynchronously wf
    override __.RunWithLogs(workflow : Cloud<unit>) =
        let logTester = new InMemoryLogTester()
        let imem = ThreadPoolRuntime.Create(fileStore = fsStore, serializer = serializer, logger = logTester, memoryEmulation = MemoryEmulation.Copied)
        imem.RunSynchronously workflow
        logTester.GetLogs()

    override __.FsCheckMaxNumberOfTests = if isCIInstance then 20 else 100
    override __.FsCheckMaxNumberOfIOBoundTests = if isCIInstance then 5 else 30