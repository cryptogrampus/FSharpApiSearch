﻿namespace FSharpApiSearch

open Microsoft.FSharp.Compiler.SourceCodeServices
open FSharpApiSearch.AssemblyLoader

type FSharpApiSearchClient(targets: string seq, dictionaries: ApiDictionary seq) =
  let dictionaries = dictionaries |> Seq.toArray
  let targetDictionaries = dictionaries |> Seq.filter (fun x -> targets |> Seq.exists ((=)x.AssemblyName)) |> Seq.toArray

  static member DefaultReferences = [
    "mscorlib" 
    "System"
    "System.Core"
    "System.Xml"
    "System.Configuration"
    "FSharp.Core"
  ]
  static member DefaultTargets = [
    "mscorlib" 
    "System"
    "System.Core"
    "FSharp.Core"
  ]

  member this.Search(query: string, options: SearchOptions) = Matcher.search dictionaries options targetDictionaries query

  member this.TargetAssemblies: string list = targetDictionaries |> Array.map (fun x -> x.AssemblyName) |> Array.toList