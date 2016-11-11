﻿namespace MBrace.Runtime.Components

open System
open System.IO
open System.IO.Compression
open System.Text.RegularExpressions
open System.Runtime.Serialization
open System.Reflection

open MBrace.FsPickler.Hashing

open MBrace.Vagabond
open MBrace.Vagabond.AssemblyProtocols

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Library

open MBrace.ThreadPool
open MBrace.Runtime
open MBrace.Runtime.Utils
open MBrace.Runtime.Utils.String

#nowarn "1571"

[<AutoOpen>]
module private Common =

    /// Gets a unique blob filename for provided assembly
    let filename (id : AssemblyId) = Vagabond.GetUniqueFileName id

    let getStoreAssemblyPath k compress id = k <| filename id + id.Extension + if compress then ".gz" else ""
    let getStoreSymbolsPath k compress id = k <| filename id + ".pdb" + if compress then ".gz" else ""
    let getStoreMetadataPath k id = k <| filename id + ".vgb"

    let getStoreDataPath prefixByAssemblySessionId k (id : AssemblyId) (hash : HashResult) =
        let fileName = Vagabond.GetUniqueFileName hash
        if prefixByAssemblySessionId then
            let truncate n (t : string) =
                if t.Length <= n then t
                else t.Substring(0, n)

            let sourceId,_,_ = Vagabond.TryParseAssemblySliceName id.FullName |> Option.get
            let prefix = sourceId.ToByteArray() |> Convert.BytesToBase32 |> truncate 13
            k <| sprintf "%s-%s.dat" prefix fileName
        else
            k <| sprintf "%s.dat" fileName

/// Assembly to file store uploader implementation
[<AutoSerializable(false)>]
type private StoreAssemblyUploader(store : ICloudFileStore, imem : ThreadPoolRuntime, logger : ISystemLogger, prefixDataByAssemblyId : bool, compressAssemblies : bool) =
    let sizeOfLocalFile (path:string) = FileInfo(path).Length |> getHumanReadableByteSize
    let getFullPath (fileName : string) = store.GetFullPath fileName

    let tryGetCurrentMetadata (id : AssemblyId) = local {
        try 
            let! c = PersistedValue.OfCloudFile<VagabondMetadata>(getStoreMetadataPath getFullPath id)
            let! md = Cloud.OfAsync <| c.GetValueAsync()
            return Some md

        with :? FileNotFoundException -> return None
    }

    let getAssemblyLoadInfo (id : AssemblyId) = local {
        let! assemblyExists = CloudFile.Exists (getStoreAssemblyPath getFullPath compressAssemblies id)
        if not assemblyExists then return NotLoaded id
        else
            let! metadata = tryGetCurrentMetadata id
            return
                match metadata with
                | None -> NotLoaded(id)
                | Some md -> Loaded(id, false, md)
    }

    /// upload assembly to blob store
    let uploadAssembly (va : VagabondAssembly) = local {
        let assemblyStorePath = getStoreAssemblyPath getFullPath compressAssemblies va.Id
        let! assemblyExists = CloudFile.Exists assemblyStorePath

        // 1. Upload assembly image.
        if not assemblyExists then
            /// print upload sizes for given assembly
            let uploadSizes = 
                seq {
                    yield sprintf "IMG %s" (sizeOfLocalFile va.Image)
                    match va.Symbols with
                    | Some s -> yield sprintf "PDB %s" (sizeOfLocalFile s)
                    | None -> ()
                } |> String.concat ", "

            logger.Logf LogLevel.Info "Uploading '%s' [%s]" va.FullName uploadSizes
            let! _ = CloudFile.Upload(va.Image, assemblyStorePath, overwrite = true, compress = compressAssemblies)
            return ()

            // 2. Upload symbols if applicable.
            match va.Symbols with
            | None -> ()
            | Some symbolsPath ->
                let symbolsStorePath = getStoreSymbolsPath getFullPath compressAssemblies va.Id
                let! symbolsExist = CloudFile.Exists symbolsStorePath
                if not symbolsExist then
                    let! _ = CloudFile.Upload(symbolsPath, symbolsStorePath, overwrite = true, compress = compressAssemblies)
                    return ()

        // 3. Upload metadata
        // check current metadata in store
        let! currentMetadata = tryGetCurrentMetadata va.Id

        // detect if metadata in blob store is stale
        let isRequiredUpdate =
            match currentMetadata with
            | None -> true
            | Some md ->
                // require a data dependency whose store generation is older than local
                (md.DataDependencies, va.Metadata.DataDependencies)
                ||> Array.exists2 (fun store local -> local.Generation > store.Generation)

        if not isRequiredUpdate then return Loaded(va.Id, false, va.Metadata) else

        // upload data dependencies
        let files = va.PersistedDataDependencies |> Map.ofArray
        let dataFiles = 
            va.Metadata.DataDependencies 
            |> Seq.choose (fun dd -> match dd.Data with Persisted hash -> Some (hash, dd) | _ -> None)
            |> Seq.groupBy fst
            |> Seq.map (fun (hash, dds) -> let dd = Seq.head dds |> snd in dd, hash, files.[dd.Id])
            |> Seq.toArray

        let uploadDataFile (dd : DataDependencyInfo, hash : HashResult, localPath : string) = local {
            let blobPath = getStoreDataPath prefixDataByAssemblyId getFullPath va.Id hash
            let! dataExists = CloudFile.Exists blobPath
            if not dataExists then
                logger.Logf LogLevel.Info "Uploading data dependency '%s' [%s]" dd.Name (sizeOfLocalFile localPath)
                let! _ = CloudFile.Upload(localPath, blobPath, overwrite = true)
                ()
        }

        // only print metadata message if updating data dependencies
        if assemblyExists then logger.Logf LogLevel.Info "Updating metadata for '%s'" va.FullName

        do! dataFiles |> Seq.map uploadDataFile |> Local.Parallel |> Local.Ignore

        // upload metadata record; TODO: use CloudAtom for synchronization?
        let! _ = PersistedValue.New<VagabondMetadata>(va.Metadata, path = getStoreMetadataPath getFullPath va.Id)
        return Loaded(va.Id, false, va.Metadata)
    }

    interface IRemoteAssemblyReceiver with
        member x.GetLoadedAssemblyInfo(dependencies: AssemblyId []): Async<AssemblyLoadInfo []> = async {
            return! dependencies |> Seq.map getAssemblyLoadInfo |> Local.Parallel |> imem.ToAsync
        }
        
        member x.PushAssemblies(assemblies: VagabondAssembly []): Async<AssemblyLoadInfo []> =  async {
            return! assemblies |> Seq.map uploadAssembly |> Local.Parallel |> imem.ToAsync
        }

/// File store assembly downloader implementation
[<AutoSerializable(false)>]
type private StoreAssemblyDownloader(store : ICloudFileStore, imem : ThreadPoolRuntime, logger : ISystemLogger, prefixDataByAssemblyId : bool, compressAssemblies : bool) =
    let getFullPath (fileName : string) = store.GetFullPath fileName

    let getImageReader (path : string) = async {
        let! stream = store.BeginRead path
        if compressAssemblies then
            return new GZipStream(stream, CompressionMode.Decompress) :> Stream
        else
            return stream
    }

    interface IAssemblyDownloader with
        member x.GetImageReader(id: AssemblyId): Async<Stream> = async {
            logger.Logf LogLevel.Info "Downloading '%s'" id.FullName
            return! getImageReader (getStoreAssemblyPath getFullPath compressAssemblies id)
        }
        
        member x.TryGetSymbolReader(id: AssemblyId): Async<Stream option> = async {
            let symbolsStorePath = getStoreSymbolsPath getFullPath compressAssemblies id
            let! exists = store.FileExists symbolsStorePath
            if exists then
                let! stream = getImageReader symbolsStorePath
                return Some stream
            else
                return None
        }
        
        member x.ReadMetadata(id: AssemblyId): Async<VagabondMetadata> = 
            local {
                let! c = PersistedValue.OfCloudFile<VagabondMetadata>(getStoreMetadataPath getFullPath id)
                return! Cloud.OfAsync <| c.GetValueAsync()
            } |> imem.ToAsync

        member x.GetPersistedDataDependencyReader(id : AssemblyId, dd : DataDependencyInfo, hash : HashResult): Async<Stream> = async {
            logger.Logf LogLevel.Info "Downloading data dependency '%s'." dd.Name
            return! store.BeginRead(getStoreDataPath prefixDataByAssemblyId getFullPath id hash)
        }

/// Distributable StoreAssemblyManagement configuration object
[<NoEquality; NoComparison; AutoSerializable(true)>]
type StoreAssemblyManagerConfiguration =
    {
        /// Store instance used for persisted vagabond data
        Store : ICloudFileStore
        /// Store directory used for storing vagabond data
        VagabondContainer : string
        /// Serializer instance used for vagabond metadata
        Serializer : ISerializer
        /// Specifies if data dependencies are to be prefixed by their assembly session identifiers.
        PrefixDataDependenciesByAssemblyId : bool
        /// Specifies that uploaded assemblies should use GZip compression when uploaded to store.
        CompressAssemblies : bool
        /// Assemblies ignored by Vagabond
        IgnoredAssemblies : Set<AssemblyId>
    }

    /// <summary>
    ///     Creates a Vagabond StoreAssemblyManager using given paramaters.
    /// </summary>
    /// <param name="store">Store instance used for persisted vagabond data.</param>
    /// <param name="serializer">Serializer instance used for vagabond metadata.</param>
    /// <param name="ignoredAssemblies">Store directory used for storing vagabond data. Defaults to "vagabond".</param>
    /// <param name="container">Store directory used for storing vagabond data. Defaults to "vagabond".</param>
    /// <param name="prefixDataDependenciesByAssemblyId">Prefix upload data dependency files by their assembly session identifiers. Defaults to true.</param>
    /// <param name="compressAssemblies">Specifies that uploaded assemblies should use GZip compression when uploaded to store. Defaults to false.</param>
    static member Create(store : ICloudFileStore, serializer : ISerializer, ?ignoredAssemblies : Assembly [], 
                            ?container : string, ?prefixDataDependenciesByAssemblyId : bool, ?compressAssemblies : bool) =

        let ignoredAssemblies = 
            defaultArg ignoredAssemblies [||]
            |> fun ia -> Vagabond.ComputeAssemblyDependencies(ia, policy = AssemblyLookupPolicy.ResolveRuntime)
            |> Seq.map Vagabond.ComputeAssemblyId 
            |> Set.ofSeq

        {
            Store = store
            Serializer = serializer
            VagabondContainer = defaultArg container "vagabond"
            PrefixDataDependenciesByAssemblyId = defaultArg prefixDataDependenciesByAssemblyId true
            CompressAssemblies = defaultArg compressAssemblies false
            IgnoredAssemblies = ignoredAssemblies
        }

/// AssemblyManager implementation that uses cloud store to share Vagabond assemblies.
/// Type is *not* serializable, transfer using the StoreAssemblyManagerConfiguration object instead.
[<Sealed; AutoSerializable(false)>]
type StoreAssemblyManager private (config : StoreAssemblyManagerConfiguration, localLogger : ISystemLogger) =
    let fileStore = config.Store.WithDefaultDirectory config.VagabondContainer
    let imem = ThreadPoolRuntime.Create(fileStore = fileStore, serializer = config.Serializer, memoryEmulation = MemoryEmulation.Shared)
    let uploader = new StoreAssemblyUploader(fileStore, imem, localLogger, config.PrefixDataDependenciesByAssemblyId, config.CompressAssemblies)
    let downloader = new StoreAssemblyDownloader(fileStore, imem, localLogger, config.PrefixDataDependenciesByAssemblyId, config.CompressAssemblies)

    /// <summary>
    ///     Creates a local StoreAssemblyManager instance with provided configuration. 
    /// </summary>
    /// <param name="config">StoreAssemblyManager configuration record.</param>
    /// <param name="localLogger">Logger used by assembly manager instance. Defaults to no logging.</param>
    static member Create(config : StoreAssemblyManagerConfiguration, ?localLogger : ISystemLogger) =
        ignore VagabondRegistry.Instance
        let localLogger = match localLogger with Some l -> l | None -> new NullLogger() :> _
        new StoreAssemblyManager(config, localLogger)

    /// <summary>
    ///     Asynchronously upload provided dependencies to store.
    /// </summary>
    /// <param name="ids">Assemblies to be uploaded.</param>
    /// <returns>List of data dependencies that failed to be serialized.</returns>
    member __.UploadAssemblies(assemblies : seq<VagabondAssembly>) : Async<DataDependencyInfo []> = async {
        let assemblies = assemblies |> Seq.filter (fun a -> not <| config.IgnoredAssemblies.Contains a.Id)
        let! errors = VagabondRegistry.Instance.SubmitDependencies(uploader, assemblies)
        if errors.Length > 0 then
            let errors = errors |> Seq.map (fun dd -> dd.Name) |> String.concat ", "
            localLogger.Logf LogLevel.Warning "Could not serialize data dependencies: %s" errors

        return errors
    }

    /// <summary>
    ///     Asynchronously download provided dependencies from store.
    /// </summary>
    /// <param name="ids">Assembly id's requested for download.</param>
    /// <returns>Vagabond assemblies downloaded to local disk.</returns>
    member __.DownloadAssemblies(ids : seq<AssemblyId>) : Async<VagabondAssembly []> = async {
        let ids = ids |> Seq.filter (not << config.IgnoredAssemblies.Contains)
        return! VagabondRegistry.Instance.DownloadAssemblies(downloader, ids)
    }

    /// Load local assemblies to current AppDomain
    member __.LoadAssemblies(assemblies : seq<VagabondAssembly>) =
        let assemblies = assemblies |> Seq.filter (fun a -> not <| config.IgnoredAssemblies.Contains a.Id)
        VagabondRegistry.Instance.LoadVagabondAssemblies(assemblies)

    /// Compute dependencies for provided object graph
    member __.ComputeDependencies(graph : 'T) : VagabondAssembly [] =
        VagabondRegistry.Instance.ComputeObjectDependencies(graph, permitCompilation = true, includeNativeDependencies = true) 
        |> Array.filter (fun a -> not <| config.IgnoredAssemblies.Contains a.Id)

    /// <summary>
    ///     Registers a native assembly dependency to client state.
    /// </summary>
    /// <param name="assemblyPath">Path to native assembly.</param>
    member __.RegisterNativeDependency(assemblyPath : string) : VagabondAssembly =
        VagabondRegistry.Instance.RegisterNativeDependency assemblyPath

    /// Gets all native dependencies registered in current instance
    member __.NativeDependencies = VagabondRegistry.Instance.NativeDependencies

    interface IAssemblyManager with
        member x.ComputeDependencies(graph: obj): VagabondAssembly [] =
            x.ComputeDependencies graph 
    
        member x.DownloadAssemblies(ids: seq<AssemblyId>): Async<VagabondAssembly []> = 
            x.DownloadAssemblies(ids)
    
        member x.LoadAssemblies(assemblies: seq<VagabondAssembly>): AssemblyLoadInfo [] = 
            x.LoadAssemblies(assemblies)
    
        member x.NativeDependencies: VagabondAssembly [] = 
            x.NativeDependencies
    
        member x.RegisterNativeDependency(path: string): VagabondAssembly =
            x.RegisterNativeDependency path
    
        member x.UploadAssemblies(assemblies: seq<VagabondAssembly>): Async<unit> = async {
            let! _ = x.UploadAssemblies assemblies
            return ()
        }