﻿namespace MyGame
open Prime
open Nu

// this is our Elm-style command type. To learn about the Elm-style, read this article here -
// https://medium.com/@bryanedds/a-game-engine-that-allows-you-to-program-in-the-elm-style-31d806fbe27f
type MyGameCommand =
    | ShowTitle
    | ShowCredits
    | ShowGameplay
    | ExitGame

// this is the game dispatcher that is customized for our game. In here, we create screens and wire
// them up with events and transitions.
type MyGameDispatcher () =
    inherit GameDispatcher<unit, unit, MyGameCommand> ()

    // here we define the Bindings used to connect events to their desired commands
    override dispatcher.Bindings (_, _, _) =
        [Simulants.TitleCredits.ClickEvent =>! ShowCredits
         Simulants.TitlePlay.ClickEvent =>! ShowGameplay
         Simulants.TitleExit.ClickEvent =>! ExitGame
         Simulants.CreditsBack.ClickEvent =>! ShowTitle
         Simulants.Back.ClickEvent =>! ShowTitle]

    // here we handle the above commands
    override dispatcher.Command (command, _, _, world) =
        let world =
            match command with
            | ShowTitle -> World.transitionScreen Simulants.Title world
            | ShowCredits -> World.transitionScreen Simulants.Credits world
            | ShowGameplay -> World.transitionScreen Simulants.Gameplay world
            | ExitGame -> World.exit world
        just world

    // here we describe the content of the game including all of its screens.
    override dispatcher.Content (_, _, _) =
        [Content.screen Simulants.Splash.Name (Splash (Default.DissolveData, Default.SplashData, Simulants.Title)) [] []
         Content.screenFromLayerFile Simulants.Title.Name (Dissolve Default.DissolveData) "Assets/Gui/Title.nulyr"
         Content.screenFromLayerFile Simulants.Credits.Name (Dissolve Default.DissolveData) "Assets/Gui/Credits.nulyr"
         Content.screen<MyGameplayDispatcher> Simulants.Gameplay.Name (Dissolve Default.DissolveData) [] []]

    // here we hint to the renderer and audio system that the 'Gui' package should be loaded ahead of time
    override dispatcher.Register (game, world) =
        let world = World.hintRenderPackageUse "Gui" world
        let world = World.hintAudioPackageUse "Gui" world
        base.Register (game, world)