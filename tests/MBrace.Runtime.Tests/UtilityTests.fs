﻿namespace MBrace.Runtime.Tests

open System
open System.Collections
open System.Collections.Generic
open System.Reflection

open Microsoft.FSharp.Reflection

open Swensen.Unquote.Assertions
open NUnit.Framework

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Core.Tests

open MBrace.Runtime
open MBrace.Runtime.Utils
open MBrace.Runtime.Utils.Reflection

open MBrace.Core.Tests

[<TestFixture>]
module ``Runtime Utilities tests`` =
        
    let private types = [|typeof<int> ; typeof<string> ; typeof<bool> ; typeof<float> ; typeof<int64>|]
    
    [<Test>]
    let ``Tuple active pattern`` () = 
        let check (is : int []) =
            if is = null || is.Length = 0 then true
            else
                let ts = is |> Array.map (fun i -> types.[abs i % types.Length])
                let result = FSharpType.MakeTupleType(ts) |> (|Tuple|_|)
                result = Some ts

        Check.QuickThrowOnFail(check, maxRuns = 20)

    [<Test>]
    let ``FSharpFunc active pattern`` () =
        let check (is : int list) =
            let types = is |> List.map (fun i -> types.[abs i % types.Length])
            match List.rev types with
            | [] | [_] -> true
            | h :: tl ->
                let rec mkFunction ft ts =
                    match ts with
                    | [] -> ft
                    | t :: ts' ->
                        let ft' = FSharpType.MakeFunctionType(t, ft)
                        mkFunction ft' ts'

                let ft = mkFunction h tl
                let result = (|FSharpFunc|_|) ft
                result = Some (tl |> List.rev |> List.toArray, h)

        Check.QuickThrowOnFail(check, maxRuns = 20)


    [<Test>]
    let ``Generic collections active pattern`` () =
        test <@ match (|CollectionWithCount|_|) [|1 .. 100|] with Some (_,count) -> count = 100 | _ -> false @>
        test <@ match (|CollectionWithCount|_|) (Array3D.zeroCreate<int> 5 5 5) with Some (_,count) -> count = 125 | _ -> false @>
        test <@ match (|CollectionWithCount|_|) (dict [(1,1); (2,1)]) with Some (_,count) -> count = 2 | _ -> false @>
        test <@ match (|CollectionWithCount|_|) (hset [1 .. 10]) with Some (_,count) -> count = 10 | _ -> false @>
        test <@ match (|CollectionWithCount|_|) (let ht = new Hashtable() in ht.Add(1,1) ; ht) with Some (_,count) -> count = 1 | _ -> false @>
        test <@ match (|CollectionWithCount|_|) (let d = new Dictionary<int,int>() in d.Add(1,1) ; d) with Some (_,count) -> count = 1 | _ -> false @>
        test <@ match (|CollectionWithCount|_|) [1 .. 100] with Some (_,count) -> count = 100 | _ -> false @>
        test <@ match (|CollectionWithCount|_|) (Map.ofList [(1,1)]) with Some (_,count) -> count = 1 | _ -> false @>
        test <@ match (|CollectionWithCount|_|) (Set.ofList [1 .. 10]) with Some (_,count) -> count = 10 | _ -> false @>