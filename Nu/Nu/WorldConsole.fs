﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2018.

namespace Nu
open System
open System.Runtime.InteropServices
open System.Text
open SDL2
open Prime
open Nu

[<RequireQualifiedAccess>]
module WorldConsole =
    
    [<DllImport("user32.dll")>]
    extern bool private SetForegroundWindow (IntPtr hWnd)

    [<DllImport("user32.dll", EntryPoint="FindWindow", SetLastError = true)>]
    extern IntPtr private FindWindowByCaption (IntPtr zeroOnly, string lpWindowName)
    
    [<DllImport("user32.dll")>]
    extern IntPtr private GetForegroundWindow ()

    [<DllImport("user32.dll")>]
    extern int private GetWindowText (IntPtr hWnd, StringBuilder text, int count)

    let private getActiveWindowTitle () =
        let buffSize = 1024
        let buff = new StringBuilder (buffSize)
        let handle = GetForegroundWindow ()
        if GetWindowText(handle, buff, buffSize) > 0
        then buff.ToString ()
        else ""

    let private showWindowByTitle title =
        let handle = FindWindowByCaption (IntPtr.Zero, title)
        if handle = IntPtr.Zero then Console.WriteLine("Can't find window with title '" + title + "'.")
        else SetForegroundWindow handle |> ignore

    let rec run gameTitle world =
        while Console.KeyAvailable do ignore (Console.ReadKey true)
        showWindowByTitle Console.Title
        Console.Write "> "
        match Console.ReadLine () with
        | input when String.IsNullOrWhiteSpace input ->
            showWindowByTitle gameTitle
            world
        | input ->
            let context = Default.Game
            let frame = context.GetScriptFrame world
            try let expr = scvalue<Scripting.Expr> input
                let struct (result, world) = World.eval expr frame context world
                Console.Write ": "
                Console.WriteLine (scstring result)
                run gameTitle world
            with exn ->
                Console.WriteLine ("Unexpected exception:\n" + scstring exn)
                run gameTitle (World.choose world)

    let tryHookUp world =
        match Environment.OSVersion.Platform with
        | PlatformID.Win32NT 
        | PlatformID.Win32Windows ->
            let world =
                World.subscribe (fun keyEvent world ->
                    if keyEvent.Data.ScanCode = int SDL.SDL_Scancode.SDL_SCANCODE_GRAVE
                    then run (getActiveWindowTitle ()) world
                    else world)
                    Events.KeyboardKeyDown
                    Default.Game
                    world
            Log.info "Console hooked up (press ` (backtick) in game to open console)."
            (true, world)
        | _ ->
            Log.info "Console not hooked up (console is unsupported on non-Windows platforms)."
            (false, world)