﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2018.

#I __SOURCE_DIRECTORY__
#r "System.Configuration"
#r "../../packages/System.ValueTuple.4.5.0/lib/portable-net40+sl4+win8+wp8/System.ValueTuple.dll"
#r "../../packages/FParsec.1.0.3/lib/net40-client/FParsecCS.dll" // MUST be referenced BEFORE FParsec.dll!
#r "../../packages/FParsec.1.0.3/lib/net40-client/FParsec.dll"
//#r "../../packages/xunit.core.2.3.1/xunit.core.2.3.1.nupkg"
//#r "../../packages/xunit.abstractions.2.0.1/xunit.abstractions.2.0.1.nupkg"
//#r "../../packages/xunit.assert.2.3.1/xunit.assert.2.3.1.nupkg"
#r "../../packages/FsCheck.2.11.0/lib/net452/FsCheck.dll"
#r "../../packages/FsCheck.Xunit.2.11.0/lib/net452/FsCheck.Xunit.dll"
#r "../../packages/Prime.3.7.1/lib/net46/Prime.exe"
#r "../../Nu/Nu.Dependencies/FSharpx.Core/FSharpx.Core.dll"
#r "../../Nu/Nu.Dependencies/FSharpx.Collections/FSharpx.Collections.dll"
#r "../../Nu/Nu.Dependencies/Farseer/FarseerPhysics.dll"
#r "../../Nu/Nu.Dependencies/Magick.NET/Magick.NET-AnyCPU.dll"
#r "../../Nu/Nu.Dependencies/Nito.Collections.Deque/Nito.Collections.Deque.dll"
#r "../../Nu/Nu.Dependencies/SDL2-CS/Debug/SDL2-CS.dll"
#r "../../Nu/Nu.Dependencies/TiledSharp/Debug/TiledSharp.dll"
#r "../../Nu/Nu.Math/bin/Debug/Nu.Math.dll"
#r "../../Nu/Nu/bin/Debug/Nu.exe"

open System
open System.IO
open FSharpx
open FSharpx.Collections
open SDL2
open TiledSharp
open Prime
open Nu
open Nu.Declarative

// set current directly to local for execution in VS F# interactive
Directory.SetCurrentDirectory (__SOURCE_DIRECTORY__ + "/bin/Debug")

// initialize Nu
Nu.init false