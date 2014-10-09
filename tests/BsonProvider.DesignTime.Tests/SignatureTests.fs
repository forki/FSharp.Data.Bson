﻿(* Copyright (c) 2013-2014 Tomas Petricek and Gustavo Guerra
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *)

module BsonProvider.DesignTime.Tests.SignatureTests

open System.IO
open MongoDB.Bson
open NUnit.Framework
open BsonProvider.ProviderImplementation

let (++) a b = Path.Combine(a, b)

let sourceDirectory = __SOURCE_DIRECTORY__

let expectedDirectory = sourceDirectory ++ "expected"

let assemblyName = "FSharp.Data.Bson.dll"
let runtimeAssembly = sourceDirectory ++ ".." ++ ".." ++ "bin" ++ assemblyName

let normalize (str:string) =
    str.Replace("\r\n", "\n").TrimEnd [| '\n' |]

let writeBytes path (samples:BsonDocument list) =
    async {
        use file = File.Open(path, FileMode.CreateNew, FileAccess.Write)
        for doc in samples do
            do! file.AsyncWrite (doc.ToBson())
    } |> Async.RunSynchronously

let validateSignature filename (samples:BsonDocument list) =
    let path = sourceDirectory ++ filename
    writeBytes path samples

    let testCase =
        sprintf "Bson,%s,0," filename
        |> TypeProviderInstantiation.Parse

    let expected =
        testCase.ExpectedPath expectedDirectory
        |> File.ReadAllText
        |> normalize

    let output =
        testCase.Dump sourceDirectory "" runtimeAssembly
                      (*signatureOnly*)true (*ignoreOutput*)false
        |> normalize

    File.Delete path

    if output <> expected then
        printfn "Obtained Signature:\n%s" output
    Assert.AreEqual(expected, output)

[<Test>]
let ``Validate signature for empty document``() =
    [ BsonDocument() ]
    |> validateSignature "empty.bson"

[<Test>]
let ``Validate signature for bool type``() =
    [ BsonDocument("field", BsonBoolean false) ]
    |> validateSignature "bool.bson"

[<Test>]
let ``Validate signature for optional bool type``() =
    [ BsonDocument("field", BsonBoolean false); BsonDocument() ]
    |> validateSignature "optional-bool.bson"

[<Test>]
let ``Validate signature for int type``() =
    [ BsonDocument("field", BsonInt32 0) ]
    |> validateSignature "int.bson"

[<Test>]
let ``Validate signature for optional int type``() =
    [ BsonDocument("field", BsonInt32 0); BsonDocument() ]
    |> validateSignature "optional-int.bson"

[<Test>]
let ``Validate signature for string type``() =
    [ BsonDocument("field", BsonString "0") ]
    |> validateSignature "string.bson"

[<Test>]
let ``Validate signature for optional string type``() =
    [ BsonDocument("field", BsonString "0"); BsonDocument() ]
    |> validateSignature "optional-string.bson"
