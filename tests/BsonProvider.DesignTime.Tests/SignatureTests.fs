﻿#if INTERACTIVE
#r "../../packages/NUnit.2.6.3/lib/nunit.framework.dll"
#r "../../bin/FSharp.Data.DesignTime.dll"
#r "../../bin/BsonProvider.DesignTime.dll"
#load "../Common/FsUnit.fs"
#else
module BsonProvider.DesignTime.Tests.SignatureTests
#endif

open System.IO
open FsUnit
open NUnit.Framework
open BsonProvider.ProviderImplementation

let (++) a b = Path.Combine(a, b)

let sourceDirectory = __SOURCE_DIRECTORY__

let testCases =
    sourceDirectory ++ "SignatureTestCases.config"
    |> File.ReadAllLines
    |> Array.map TypeProviderInstantiation.Parse

let expectedDirectory = sourceDirectory ++ "expected"

let resolutionFolder = ""
let assemblyName = "FSharp.Data.Bson.dll"
let runtimeAssembly = sourceDirectory ++ ".." ++ ".." ++ "bin" ++ assemblyName
let portable47RuntimeAssembly = sourceDirectory ++ ".." ++ ".." ++ "bin" ++ "portable47" ++ assemblyName

let generateAllExpected() =
    if not <| Directory.Exists expectedDirectory then
        Directory.CreateDirectory expectedDirectory |> ignore
    for testCase in testCases do
        testCase.Dump resolutionFolder expectedDirectory runtimeAssembly (*signatureOnly*)false (*ignoreOutput*)false
        |> ignore

let normalize (str:string) =
  str.Replace("\r\n", "\n").Replace("\r", "\n").Replace("@\"<RESOLUTION_FOLDER>\"", "\"<RESOLUTION_FOLDER>\"")

[<Test>]
[<TestCaseSource "testCases">]
let ``Validate signature didn't change `` (testCase:TypeProviderInstantiation) =
    let expected = testCase.ExpectedPath expectedDirectory |> File.ReadAllText |> normalize
    let output = testCase.Dump resolutionFolder "" runtimeAssembly (*signatureOnly*)false (*ignoreOutput*)false |> normalize
    if output <> expected then
        printfn "Obtained Signature:\n%s" output
    output |> should equal expected

[<Test>]
[<TestCaseSource "testCases">]
let ``Generating expressions works in portable profile 47 `` (testCase:TypeProviderInstantiation) =
    testCase.Dump resolutionFolder "" portable47RuntimeAssembly (*signatureOnly*)false (*ignoreOutput*)true |> ignore
