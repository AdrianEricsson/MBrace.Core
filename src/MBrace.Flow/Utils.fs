﻿namespace MBrace.Flow.Internals

open System
open System.Threading
open System.Threading.Tasks
open System.Collections
open System.Collections.Generic

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Flow

[<AutoOpen>]
module Utils =

    type internal OAttribute = System.Runtime.InteropServices.OptionalAttribute
    type internal DAttribute = System.Runtime.InteropServices.DefaultParameterValueAttribute

    type Collector<'T, 'R> with
        /// Converts MBrace.Flow.Collector to Nessos.Streams.Collector
        member collector.ToParStreamCollector () =
            { new Nessos.Streams.ParCollector<'T, 'R> with
                member self.DegreeOfParallelism = Environment.ProcessorCount
                member self.Iterator() = collector.Iterator()
                member self.Result = collector.Result }

    module Option =
        /// converts nullable to optional
        let ofNullable (t : Nullable<'T>) =
            if t.HasValue then Some t.Value
            else None

    module ResizeArray =
        
        [<AutoSerializable(false)>]
        type private ResizeArrayConcatenator<'T>(inputs : ResizeArray<'T> []) =
            static let empty = [|new ResizeArray<'T>()|]
            let inputs = if Array.isEmpty inputs then empty else inputs
            let mutable lp = 0
            let mutable ep = -1
            let mutable l = inputs.[0]

            interface IEnumerator<'T> with
                member x.Current: 'T = l.[ep]
                member x.Current: obj = l.[ep] :> obj
                member x.Dispose(): unit = ()
                member x.MoveNext(): bool =
                    if ep + 1 = l.Count then
                        // shift to next List, skipping empty occurences
                        lp <- lp + 1
                        while lp < inputs.Length && inputs.[lp].Count = 0 do
                            lp <- lp + 1
                            
                        if lp = inputs.Length then false
                        else
                            l <- inputs.[lp]
                            ep <- 0
                            true
                    else
                        ep <- ep + 1
                        true
                
                member x.Reset(): unit =
                    lp <- 0
                    l <- inputs.[0]
                    ep <- -1

        /// Ad-hoc ResizeArray concatenation combinator
        let concat (inputs : seq<ResizeArray<'T>>) : seq<'T> =
            let inputs = Seq.toArray inputs
            Seq.fromEnumerator (fun () -> new ResizeArrayConcatenator<'T>(inputs) :> _)

    /// generates a human readable string for byte sizes
    /// including a KiB, MiB, GiB or TiB suffix depending on size
    let internal getHumanReadableByteSize (size : int64) =
        if size <= 512L then sprintf "%d bytes" size
        elif size <= 512L * 1024L then sprintf "%.2f KiB" (decimal size / decimal 1024L)
        elif size <= 512L * 1024L * 1024L then sprintf "%.2f MiB" (decimal size / decimal (1024L * 1024L))
        elif size <= 512L * 1024L * 1024L * 1024L then sprintf "%.2f GiB" (decimal size / decimal (1024L * 1024L * 1024L))
        else sprintf "%.2f TiB" (decimal size / decimal (1024L * 1024L * 1024L * 1024L))
        

    module Partition =

        /// partition elements so that size in accumulated groupings does not surpass maxSize
        let partitionBySize (getSize : 'T -> Async<int64>) (maxSize : int64) (inputs : 'T []) = async {
            if maxSize <= 0L then invalidArg "maxSize" "Must be positive value."

            let rec aux i accSize (accElems : 'T list) (accGroupings : 'T list list) = async {
                if i >= inputs.Length then return accElems :: accGroupings
                else
                    let t = inputs.[i]
                    let! size = getSize t
                    // if size of element alone exceeds maximum, then incorporate in current grouping provided it is sufficiently small.
                    if size >= maxSize && accSize < 5L * maxSize then return! aux (i + 1) 0L [] ((t :: accElems) :: accGroupings)
                    // accumulated length exceeds limit, flush accumulated elements to groupings and retry
                    elif accSize + size > maxSize then return! aux (i + 1) size [t] (accElems :: accGroupings)
                    // within limit, append to accumulated elements
                    else return! aux (i + 1) (accSize + size) (t :: accElems) accGroupings
            }

            let! groupings = aux 0 0L [] []
            return
                groupings
                |> List.rev
                |> Seq.map (fun gp -> gp |> List.rev |> List.toArray)
                |> Seq.toArray
        }

    module Collection =
        let map (f : 'T -> 'S) (collection : ICollection<'T>) : ICollection<'S> =
            let mapped = Seq.map f collection
            { new ICollection<'S> with
                  member x.Count: int = collection.Count
                  member x.GetEnumerator(): IEnumerator = 
                      (mapped :> IEnumerable).GetEnumerator() : IEnumerator
                  member x.GetEnumerator(): IEnumerator<'S> = 
                      mapped.GetEnumerator()
                  member x.IsReadOnly: bool = true
                  member x.Add(_: 'S): unit = 
                      raise (System.NotSupportedException())
                  member x.Remove(item: 'S): bool = 
                      raise (System.NotSupportedException())
                  member x.Clear(): unit = 
                      raise (System.NotSupportedException())
                  member x.Contains(item: 'S): bool = 
                      raise (System.NotSupportedException())
                  member x.CopyTo(array: 'S [], arrayIndex: int): unit = 
                      raise (System.NotSupportedException())
            }

        let splitByPartitionCount (partitionCount : int) (collection : ICollection<'T>) : seq<'T []> =
            if partitionCount <= 0 then 
                if collection.Count = 0 then Seq.empty
                else raise <| ArgumentOutOfRangeException("partitionCount")
            else

            let N = collection.Count
            let div = N / partitionCount
            let rem = N % partitionCount
            let mutable p = 0
            let mutable i = 0
            let results = new ResizeArray<'T []> ()
            let acc = new ResizeArray<'T> ()
            for t in collection do
                acc.Add t
                if i + 1 = div + (if p < rem then 1 else 0) then
                    results.Add (acc.ToArray())
                    acc.Clear()
                    p <- p + 1
                    i <- 0
                else
                    i <- i + 1

            results :> _