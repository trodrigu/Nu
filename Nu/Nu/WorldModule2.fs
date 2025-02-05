﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2018.

namespace Nu
open System
open System.Collections.Generic
open System.IO
#if MULTITHREAD
open System.Threading.Tasks
#endif
open SDL2
open Prime
open global.Nu

[<AutoOpen; ModuleBinding>]
module WorldModule2 =

    let private ScreenTransitionMouseLeftKey = makeGuid ()
    let private ScreenTransitionMouseCenterKey = makeGuid ()
    let private ScreenTransitionMouseRightKey = makeGuid ()
    let private ScreenTransitionMouseX1Key = makeGuid ()
    let private ScreenTransitionMouseX2Key = makeGuid ()
    let private ScreenTransitionKeyboardKeyKey = makeGuid ()
    let private SplashScreenUpdateKey = makeGuid ()

    type World with

        static member internal makeEntityTree () =
            SpatialTree.make Constants.Engine.EntityTreeGranularity Constants.Engine.EntityTreeDepth Constants.Engine.EntityTreeBounds

        static member internal rebuildEntityTree world =
            let omniEntities =
                match World.getOmniScreenOpt world with
                | Some screen -> World.getLayers screen world |> Seq.map (flip World.getEntities world) |> Seq.concat
                | None -> Seq.empty
            let selectedEntities =
                match World.getSelectedScreenOpt world with
                | Some screen -> World.getLayers screen world |> Seq.map (flip World.getEntities world) |> Seq.concat
                | None -> Seq.empty
            let entities = Seq.append omniEntities selectedEntities
            let tree = World.makeEntityTree ()
            for entity in entities do
                let boundsMax = entity.GetBoundsMax world
                SpatialTree.addElement (entity.GetOmnipresent world || entity.GetViewType world = Absolute) boundsMax entity tree
            tree

        /// Sort subscriptions by their depth priority.
        static member sortSubscriptionsByDepth subscriptions world =
            World.sortSubscriptionsBy
                (fun (participant : Participant) _ ->
                    let priority =
                        match participant with
                        | :? GlobalParticipantGeneralized
                        | :? Game -> { SortDepth = Constants.Engine.GameSortPriority; SortPositionY = 0.0f; SortTarget = Default.Game }
                        | :? Screen as screen -> { SortDepth = Constants.Engine.ScreenSortPriority; SortPositionY = 0.0f; SortTarget = screen }
                        | :? Layer as layer -> { SortDepth = Constants.Engine.LayerSortPriority + layer.GetDepth world; SortPositionY = 0.0f; SortTarget = layer }
                        | :? Entity as entity -> { SortDepth = entity.GetDepthLayered world; SortPositionY = 0.0f; SortTarget = entity }
                        | _ -> failwithumf ()
                    priority :> IComparable)
                subscriptions
                world
    
        /// Resolve a relation to an address in the current script context.
        static member resolve relation world =
            let scriptContext = World.getScriptContext world
            let address = Relation.resolve scriptContext.SimulantAddress relation
            address

        /// Resolve a Participant relation to an address in the current script context.
        [<FunctionBinding "resolve">]
        static member resolveGeneric (relation : obj Relation) world =
            World.resolve relation world

        /// Send a message to the renderer to reload its rendering assets.
        [<FunctionBinding>]
        static member reloadAssets world =
            let world = World.reloadRenderAssets world
            let world = World.reloadAudioAssets world
            World.reloadSymbols world
            world

        /// Try to check that the selected screen is idling; that is, neither transitioning in or
        /// out via another screen.
        [<FunctionBinding>]
        static member tryGetIsSelectedScreenIdling world =
            match World.getSelectedScreenOpt world with
            | Some selectedScreen -> Some (selectedScreen.IsIdling world)
            | None -> None

        /// Try to check that the selected screen is transitioning.
        [<FunctionBinding>]
        static member tryGetIsSelectedScreenTransitioning world =
            Option.map not (World.tryGetIsSelectedScreenIdling world)

        /// Check that the selected screen is idling; that is, neither transitioning in or
        /// out via another screen (failing with an exception if no screen is selected).
        [<FunctionBinding>]
        static member isSelectedScreenIdling world =
            match World.tryGetIsSelectedScreenIdling world with
            | Some answer -> answer
            | None -> failwith "Cannot query state of non-existent selected screen."

        /// Check that the selected screen is transitioning (failing with an exception if no screen
        /// is selected).
        [<FunctionBinding>]
        static member isSelectedScreenTransitioning world =
            not (World.isSelectedScreenIdling world)

        static member private setScreenTransitionStatePlus state (screen : Screen) world =
            let world = screen.SetTransitionState state world
            match state with
            | IdlingState ->
                let world = World.unsubscribe ScreenTransitionMouseLeftKey world
                let world = World.unsubscribe ScreenTransitionMouseCenterKey world
                let world = World.unsubscribe ScreenTransitionMouseRightKey world
                let world = World.unsubscribe ScreenTransitionMouseX1Key world
                let world = World.unsubscribe ScreenTransitionMouseX2Key world
                let world = World.unsubscribe ScreenTransitionKeyboardKeyKey world
                world
            | IncomingState
            | OutgoingState ->
                let world = World.subscribePlus ScreenTransitionMouseLeftKey World.handleAsSwallow (stoa<MouseButtonData> "Mouse/Left/@/Event") Default.Game world |> snd
                let world = World.subscribePlus ScreenTransitionMouseCenterKey World.handleAsSwallow (stoa<MouseButtonData> "Mouse/Center/@/Event") Default.Game world |> snd
                let world = World.subscribePlus ScreenTransitionMouseRightKey World.handleAsSwallow (stoa<MouseButtonData> "Mouse/Right/@/Event") Default.Game world |> snd
                let world = World.subscribePlus ScreenTransitionMouseX1Key World.handleAsSwallow (stoa<MouseButtonData> "Mouse/X1/@/Event") Default.Game world |> snd
                let world = World.subscribePlus ScreenTransitionMouseX2Key World.handleAsSwallow (stoa<MouseButtonData> "Mouse/X2/@/Event") Default.Game world |> snd
                let world = World.subscribePlus ScreenTransitionKeyboardKeyKey World.handleAsSwallow (stoa<KeyboardKeyData> "KeyboardKey/@/Event") Default.Game world |> snd
                world

        /// Select the given screen without transitioning, even if another transition is taking place.
        [<FunctionBinding>]
        static member selectScreen screen world =
            let world =
                match World.getSelectedScreenOpt world with
                | Some selectedScreen ->
                    let eventTrace = EventTrace.record4 "World" "selectScreen" "Deselect" EventTrace.empty
                    World.publish () (Events.Deselect --> selectedScreen) eventTrace selectedScreen world
                | None -> world
            let world = World.setScreenTransitionStatePlus IncomingState screen world
            let world = World.setSelectedScreen screen world
            let eventTrace = EventTrace.record4 "World" "selectScreen" "Select" EventTrace.empty
            World.publish () (Events.Select --> screen) eventTrace screen world

        /// Try to transition to the given screen if no other transition is in progress.
        [<FunctionBinding>]
        static member tryTransitionScreen destination world =
            match World.getSelectedScreenOpt world with
            | Some selectedScreen ->
                if World.getScreenExists selectedScreen world then
                    let subscriptionKey = makeGuid ()
                    let subscription = fun (_ : Event<unit, Screen>) world ->
                        match World.getScreenTransitionDestinationOpt world with
                        | Some destination ->
                            let world = World.unsubscribe subscriptionKey world
                            let world = World.setScreenTransitionDestinationOpt None world
                            let world = World.selectScreen destination world
                            (Cascade, world)
                        | None -> failwith "No valid ScreenTransitionDestinationOpt during screen transition!"
                    let world = World.setScreenTransitionDestinationOpt (Some destination) world
                    let world = World.setScreenTransitionStatePlus OutgoingState selectedScreen world
                    let world = World.subscribePlus<unit, Screen> subscriptionKey subscription (Events.OutgoingFinish --> selectedScreen) selectedScreen world |> snd
                    (true, world)
                else (false, world)
            | None -> (false, world)

        /// Transition to the given screen (failing with an exception if another transition is in
        /// progress).
        [<FunctionBinding>]
        static member transitionScreen destination world =
            World.tryTransitionScreen destination world |> snd
            
        // TODO: replace this with more sophisticated use of handleAsScreenTransition4, and so on for its brethren.
        static member private handleAsScreenTransitionFromSplash4<'a, 's when 's :> Simulant> handling destination (_ : Event<'a, 's>) world =
            let world = World.selectScreen destination world
            (handling, world)

        /// A procedure that can be passed to an event handler to specify that an event is to
        /// result in a transition to the given destination screen.
        static member handleAsScreenTransitionFromSplash<'a, 's when 's :> Simulant> destination evt world =
            World.handleAsScreenTransitionFromSplash4<'a, 's> Cascade destination evt world

        /// A procedure that can be passed to an event handler to specify that an event is to
        /// result in a transition to the given destination screen, as well as with additional
        /// handling provided via the 'by' procedure.
        static member handleAsScreenTransitionFromSplashBy<'a, 's when 's :> Simulant> by destination evt (world : World) =
            let (handling, world) = by evt world
            World.handleAsScreenTransitionFromSplash4<'a, 's> handling destination evt world

        static member private handleAsScreenTransitionPlus<'a, 's when 's :> Simulant>
            handling destination (_ : Event<'a, 's>) world =
            match World.tryTransitionScreen destination world with
            | (true, world) -> (handling, world)
            | (false, world) ->
                Log.trace ("Program Error: Invalid screen transition for destination address '" + scstring destination.ScreenAddress + "'.")
                (handling, world)

        /// A procedure that can be passed to an event handler to specify that an event is to
        /// result in a transition to the given destination screen.
        static member handleAsScreenTransition<'a, 's when 's :> Simulant> destination evt world =
            World.handleAsScreenTransitionPlus<'a, 's> Cascade destination evt world |> snd

        static member private updateScreenTransition3 (screen : Screen) transition world =
            let transitionTicks = screen.GetTransitionTicks world
            if transitionTicks = transition.TransitionLifetime then
                (true, screen.SetTransitionTicks 0L world)
            elif transitionTicks > transition.TransitionLifetime then
                Log.debug ("TransitionLifetime for screen '" + scstring screen.ScreenAddress + "' must be a consistent multiple of TickRate.")
                (true, screen.SetTransitionTicks 0L world)
            else (false, screen.SetTransitionTicks (transitionTicks + World.getTickRate world) world)

        static member private updateScreenTransition2 (selectedScreen : Screen) world =
            match selectedScreen.GetTransitionState world with
            | IncomingState ->
                match World.getLiveness world with
                | Running ->
                    let world =
                        if selectedScreen.GetTransitionTicks world = 0L then
                            let eventTrace = EventTrace.record4 "World" "updateScreenTransition" "IncomingStart" EventTrace.empty
                            World.publish () (Events.IncomingStart --> selectedScreen) eventTrace selectedScreen world
                        else world
                    match World.getLiveness world with
                    | Running ->
                        let (finished, world) = World.updateScreenTransition3 selectedScreen (selectedScreen.GetIncoming world) world
                        if finished then
                            let eventTrace = EventTrace.record4 "World" "updateScreenTransition" "IncomingFinish" EventTrace.empty
                            let world = World.setScreenTransitionStatePlus IdlingState selectedScreen world
                            World.publish () (Events.IncomingFinish --> selectedScreen) eventTrace selectedScreen world
                        else world
                    | Exiting -> world
                | Exiting -> world
            | OutgoingState ->
                let world =
                    if selectedScreen.GetTransitionTicks world = 0L then
                        let eventTrace = EventTrace.record4 "World" "updateScreenTransition" "OutgoingStart" EventTrace.empty
                        World.publish () (Events.OutgoingStart --> selectedScreen) eventTrace selectedScreen world
                    else world
                match World.getLiveness world with
                | Running ->
                    let (finished, world) = World.updateScreenTransition3 selectedScreen (selectedScreen.GetOutgoing world) world
                    if finished then
                        let world = World.setScreenTransitionStatePlus IdlingState selectedScreen world
                        match World.getLiveness world with
                        | Running ->
                            let eventTrace = EventTrace.record4 "World" "updateScreenTransition" "OutgoingFinish" EventTrace.empty
                            World.publish () (Events.OutgoingFinish --> selectedScreen) eventTrace selectedScreen world
                        | Exiting -> world
                    else world
                | Exiting -> world
            | IdlingState -> world

        static member private handleSplashScreenIdleUpdate idlingTime ticks evt world =
            let world = World.unsubscribe SplashScreenUpdateKey world
            if ticks < idlingTime then
                let subscription = World.handleSplashScreenIdleUpdate idlingTime (inc ticks)
                let world = World.subscribePlus SplashScreenUpdateKey subscription evt.Address evt.Subscriber world |> snd
                (Cascade, world)
            else
                match World.getSelectedScreenOpt world with
                | Some selectedScreen ->
                    if World.getScreenExists selectedScreen world then
                        let world = World.setScreenTransitionStatePlus OutgoingState selectedScreen world
                        (Cascade, world)
                    else
                        Log.trace "Program Error: Could not handle splash screen update due to no selected screen."
                        (Resolve, World.exit world)
                | None ->
                    Log.trace "Program Error: Could not handle splash screen update due to no selected screen."
                    (Resolve, World.exit world)

        static member private handleSplashScreenIdle idlingTime (splashScreen : Screen) evt world =
            let world = World.subscribePlus SplashScreenUpdateKey (World.handleSplashScreenIdleUpdate idlingTime 0L) (Events.Update --> splashScreen) evt.Subscriber world |> snd
            (Cascade, world)

        /// Set the splash aspects of a screen.
        [<FunctionBinding>]
        static member setScreenSplash splashDataOpt destination (screen : Screen) world =
            let splashLayer = screen / "SplashLayer"
            let splashLabel = splashLayer / "SplashLabel"
            let world = World.destroyLayerImmediate splashLayer world
            match splashDataOpt with
            | Some splashData ->
                let cameraEyeSize = World.getEyeSize world
                let world = World.createLayer<LayerDispatcher> (Some splashLayer.Name) screen world |> snd
                let world = splashLayer.SetPersistent false world
                let world = World.createEntity<LabelDispatcher> (Some splashLabel.Name) DefaultOverlay splashLayer world |> snd
                let world = splashLabel.SetPersistent false world
                let world = splashLabel.SetSize cameraEyeSize world
                let world = splashLabel.SetPosition (-cameraEyeSize * 0.5f) world
                let world = splashLabel.SetLabelImage splashData.SplashImage world
                let (unsub, world) = World.monitorPlus (World.handleSplashScreenIdle splashData.IdlingTime screen) (Events.IncomingFinish --> screen) screen world
                let (unsub2, world) = World.monitorPlus (World.handleAsScreenTransitionFromSplash destination) (Events.OutgoingFinish --> screen) screen world
                let world = World.monitor (fun _ -> unsub >> unsub2) (Events.Unregistering --> splashLayer) screen world
                world
            | None -> world

        /// Create a dissolve screen whose content is loaded from the given layer file.
        [<FunctionBinding>]
        static member createDissolveScreenFromLayerFile6 dispatcherName nameOpt dissolveData layerFilePath world =
            let (dissolveScreen, world) = World.createDissolveScreen5 dispatcherName nameOpt dissolveData world
            let world = World.readLayerFromFile layerFilePath None dissolveScreen world |> snd
            (dissolveScreen, world)

        /// Create a dissolve screen whose content is loaded from the given layer file.
        [<FunctionBinding>]
        static member createDissolveScreenFromLayerFile<'d when 'd :> ScreenDispatcher> nameOpt dissolveData layerFilePath world =
            World.createDissolveScreenFromLayerFile6 typeof<'d>.Name nameOpt dissolveData layerFilePath world

        /// Create a splash screen that transitions to the given destination upon completion.
        [<FunctionBinding>]
        static member createSplashScreen6 dispatcherName nameOpt splashData destination world =
            let (splashScreen, world) = World.createDissolveScreen5 dispatcherName nameOpt splashData.DissolveData world
            let world = World.setScreenSplash (Some splashData) destination splashScreen world
            (splashScreen, world)

        /// Create a splash screen that transitions to the given destination upon completion.
        [<FunctionBinding>]
        static member createSplashScreen<'d when 'd :> ScreenDispatcher> nameOpt splashData destination world =
            World.createSplashScreen6 typeof<'d>.Name nameOpt splashData destination world

        static member internal handleSubscribeAndUnsubscribe event world =
            // here we need to update the event publish flags for entities based on whether there are subscriptions to
            // these events. These flags exists solely for efficiency reasons. We also look for subscription patterns
            // that these optimization do not support, and warn the developer if they are invoked. Additionally, we
            // warn if the user attempts to subscribe to a Change event with a wildcard as doing so is not supported.
            let eventAddress = event.Data
            let eventNames = Address.getNames eventAddress
            match eventNames with
            | [|eventFirstName; _; screenName; layerName; entityName|] ->
                let entity = Entity [|screenName; layerName; entityName|]
                match eventFirstName with
                | "Update" ->
                    if Array.contains (Address.head Events.Wildcard) eventNames then
                        Log.debug
                            ("Subscribing to entity update events with a wildcard is not supported. " +
                             "This will cause a bug where some entity update events are not published.")
                    World.updateEntityPublishUpdateFlag entity world
                | "PostUpdate" ->
                    if Array.contains (Address.head Events.Wildcard) eventNames then
                        Log.debug
                            ("Subscribing to entity post-update events with a wildcard is not supported. " +
                             "This will cause a bug where some entity post-update events are not published.")
                    World.updateEntityPublishPostUpdateFlag entity world
                | _ -> world
            | eventNames when eventNames.Length >= 3 ->
                let eventFirstName = eventNames.[0]
                let eventSecondName = eventNames.[1]
                match eventFirstName with
                | "Change" when eventSecondName <> "ParentNodeOpt" ->
                    if Array.contains (Address.head Events.Wildcard) eventNames then
                        Log.debug "Subscribing to change events with a wildcard is not supported."
                    world
                | _ -> world
            | _ -> world

        static member internal makeIntrinsicOverlays facets entityDispatchers =
            let requiresFacetNames = fun sourceType -> sourceType = typeof<EntityDispatcher>
            let facets = Map.toValueListBy (fun facet -> facet :> obj) facets
            let entityDispatchers = Map.toValueListBy box entityDispatchers
            let sources = facets @ entityDispatchers
            let sourceTypes = List.map (fun source -> source.GetType ()) sources
            Reflection.makeIntrinsicOverlays requiresFacetNames sourceTypes

        /// Try to reload the overlayer currently in use by the world.
        static member tryReloadOverlays inputDirectory outputDirectory world =
            
            // attempt to reload overlay file
            let inputOverlayerFilePath = Path.Combine (inputDirectory, Assets.OverlayerFilePath)
            let outputOverlayerFilePath = Path.Combine (outputDirectory, Assets.OverlayerFilePath)
            try File.Copy (inputOverlayerFilePath, outputOverlayerFilePath, true)

                // cache old overlayer and make new one
                let oldOverlayer = World.getOverlayer world
                let entityDispatchers = World.getEntityDispatchers world
                let facets = World.getFacets world
                let intrinsicOverlays = World.makeIntrinsicOverlays facets entityDispatchers
                match Overlayer.tryMakeFromFile intrinsicOverlays outputOverlayerFilePath with
                | Right overlayer ->
                
                    // update overlayer and apply overlays to all entities
                    let world = World.setOverlayer overlayer world
                    let entities = World.getEntities1 world
                    let world = Seq.fold (World.applyEntityOverlay oldOverlayer overlayer) world entities
                    (Right overlayer, world)

                // propagate errors
                | Left error -> (Left error, world)
            with exn -> (Left (scstring exn), World.choose world)

        /// Try to reload the prelude currently in use by the world.
        static member tryReloadPrelude inputDirectory outputDirectory world =
            let inputPreludeFilePath = Path.Combine (inputDirectory, Assets.PreludeFilePath)
            let outputPreludeFilePath = Path.Combine (outputDirectory, Assets.PreludeFilePath)
            try File.Copy (inputPreludeFilePath, outputPreludeFilePath, true)
                match World.tryEvalPrelude world with
                | Right struct (preludeStr, world) -> (Right preludeStr, world)
                | Left struct (error, world) -> (Left error, world)
            with exn -> (Left (scstring exn), World.choose world)

        /// Attempt to reload the asset graph.
        /// Currently does not support reloading of song assets, and possibly others that are
        /// locked by the engine's subsystems.
        static member tryReloadAssetGraph inputDirectory outputDirectory refinementDirectory world =
            
            // attempt to reload asset graph file
            try File.Copy
                    (Path.Combine (inputDirectory, Assets.AssetGraphFilePath),
                     Path.Combine (outputDirectory, Assets.AssetGraphFilePath), true)

                // attempt to load asset graph
                match AssetGraph.tryMakeFromFile Assets.AssetGraphFilePath with
                | Right assetGraph ->

                    // build assets reload asset metadata
                    AssetGraph.buildAssets inputDirectory outputDirectory refinementDirectory false assetGraph
                    let metadata = Metadata.make assetGraph
                    let world = World.setMetadata metadata world
                    let world = World.reloadRenderAssets world
                    let world = World.reloadAudioAssets world
                    World.reloadSymbols world
                    let world = World.publish () Events.AssetsReload (EventTrace.record "World" "publishAssetsReload" EventTrace.empty) Default.Game world
                    (Right assetGraph, world)
        
                // propagate errors
                | Left error -> (Left error, world)
            with exn -> (Left (scstring exn), World.choose world)

        /// Clear all messages in all subsystems.
        static member clearMessages world =
             let world = World.updatePhysicsEngine Subsystem.clearMessages world
             let world = World.updateRenderer Subsystem.clearMessages world
             let world = World.updateAudioPlayer Subsystem.clearMessages world
             world
        
        /// Freeze the state of a world.
        static member freeze world =
            // not sure if we really want to also clear physics messages here - we didn't
            // used to
            World.clearMessages world

        /// Thaw the state of a world.
        static member thaw world =

            // because there is an optimization that makes event context mutable, operations
            // on the exist current world may affect past and future ones if the containing event
            // world incidentally isn't copied. Therefore, we restore the initial event context
            // here.
            World.continueEventSystemHack world

            // clear existing physics messages
            let world = World.updatePhysicsEngine Subsystem.clearMessages world

            // rebuild physics state
            let world = World.enqueuePhysicsMessage RebuildPhysicsHackMessage world

            // propagate current physics state
            let entities = World.getEntities1 world
            let world = Seq.fold (fun world (entity : Entity) -> entity.PropagatePhysics world) world entities
            world

        static member private processTasklet (taskletsNotRun, world) tasklet =
            let tickTime = World.getTickTime world
            if tickTime = tasklet.ScheduledTime then
                let world = tasklet.Command.Execute world
                (taskletsNotRun, world)
            elif tickTime > tasklet.ScheduledTime then
                Log.debug ("Tasklet leak found for time '" + scstring tickTime + "'.")
                (taskletsNotRun, world)
            else (UList.add tasklet taskletsNotRun, world)

        static member private processTasklets world =
            let tasklets = World.getTasklets world
            let world = World.clearTasklets world
            let (taskletsNotRun, world) = UList.fold World.processTasklet (UList.makeEmpty (UList.getConfig tasklets), world) tasklets
            World.restoreTasklets taskletsNotRun world

        /// Process an input event from SDL and ultimately publish any related game events.
        static member private processInput2 (evt : SDL.SDL_Event) world =
            let world =
                match evt.``type`` with
                | SDL.SDL_EventType.SDL_QUIT ->
                    World.exit world
                | SDL.SDL_EventType.SDL_MOUSEMOTION ->
                    let mousePosition = Vector2 (single evt.button.x, single evt.button.y)
                    let world =
                        if World.isMouseButtonDown MouseLeft world then
                            let eventTrace = EventTrace.record4 "World" "processInput" "MouseDrag" EventTrace.empty
                            World.publishPlus World.sortSubscriptionsByDepth { MouseMoveData.Position = mousePosition } Events.MouseDrag eventTrace Default.Game true world
                        else world
                    let eventTrace = EventTrace.record4 "World" "processInput" "MouseMove" EventTrace.empty
                    World.publishPlus World.sortSubscriptionsByDepth { MouseMoveData.Position = mousePosition } Events.MouseMove eventTrace Default.Game true world
                | SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN ->
                    let mousePosition = World.getMousePositionF world
                    let mouseButton = World.toNuMouseButton (uint32 evt.button.button)
                    let mouseButtonDownEvent = stoa<MouseButtonData> ("Mouse/" + MouseButton.toEventName mouseButton + "/Down/Event")
                    let mouseButtonChangeEvent = stoa<MouseButtonData> ("Mouse/" + MouseButton.toEventName mouseButton + "/Change/Event")
                    let eventData = { Position = mousePosition; Button = mouseButton; Down = true }
                    let eventTrace = EventTrace.record4 "World" "processInput" "MouseButtonDown" EventTrace.empty
                    let world = World.publishPlus World.sortSubscriptionsByDepth eventData mouseButtonDownEvent eventTrace Default.Game true world
                    let eventTrace = EventTrace.record4 "World" "processInput" "MouseButtonChange" EventTrace.empty
                    World.publishPlus World.sortSubscriptionsByDepth eventData mouseButtonChangeEvent eventTrace Default.Game true world
                | SDL.SDL_EventType.SDL_MOUSEBUTTONUP ->
                    let mousePosition = World.getMousePositionF world
                    let mouseButton = World.toNuMouseButton (uint32 evt.button.button)
                    let mouseButtonUpEvent = stoa<MouseButtonData> ("Mouse/" + MouseButton.toEventName mouseButton + "/Up/Event")
                    let mouseButtonChangeEvent = stoa<MouseButtonData> ("Mouse/" + MouseButton.toEventName mouseButton + "/Change/Event")
                    let eventData = { Position = mousePosition; Button = mouseButton; Down = false }
                    let eventTrace = EventTrace.record4 "World" "processInput" "MouseButtonUp" EventTrace.empty
                    let world = World.publishPlus World.sortSubscriptionsByDepth eventData mouseButtonUpEvent eventTrace Default.Game true world
                    let eventTrace = EventTrace.record4 "World" "processInput" "MouseButtonChange" EventTrace.empty
                    World.publishPlus World.sortSubscriptionsByDepth eventData mouseButtonChangeEvent eventTrace Default.Game true world
                | SDL.SDL_EventType.SDL_KEYDOWN ->
                    let keyboard = evt.key
                    let key = keyboard.keysym
                    let eventData = { ScanCode = int key.scancode; Repeated = keyboard.repeat <> byte 0; Down = true }
                    let eventTrace = EventTrace.record4 "World" "processInput" "KeyboardKeyDown" EventTrace.empty
                    let world = World.publishPlus World.sortSubscriptionsByHierarchy eventData Events.KeyboardKeyDown eventTrace Default.Game true world
                    let eventTrace = EventTrace.record4 "World" "processInput" "KeyboardKeyChange" EventTrace.empty
                    World.publishPlus World.sortSubscriptionsByHierarchy eventData Events.KeyboardKeyChange eventTrace Default.Game true world
                | SDL.SDL_EventType.SDL_KEYUP ->
                    let keyboard = evt.key
                    let key = keyboard.keysym
                    let eventData = { ScanCode = int key.scancode; Repeated = keyboard.repeat <> byte 0; Down = false }
                    let eventTrace = EventTrace.record4 "World" "processInput" "KeyboardKeyUp" EventTrace.empty
                    let world = World.publishPlus World.sortSubscriptionsByHierarchy eventData Events.KeyboardKeyUp eventTrace Default.Game true world
                    let eventTrace = EventTrace.record4 "World" "processInput" "KeyboardKeyChange" EventTrace.empty
                    World.publishPlus World.sortSubscriptionsByHierarchy eventData Events.KeyboardKeyChange eventTrace Default.Game true world
                | SDL.SDL_EventType.SDL_JOYHATMOTION ->
                    let index = evt.jhat.which
                    let direction = evt.jhat.hatValue
                    let eventData = { GamepadDirection = GamepadState.toNuDirection direction }
                    let eventTrace = EventTrace.record4 "World" "processInput" "GamepadDirectionChange" EventTrace.empty
                    World.publishPlus World.sortSubscriptionsByHierarchy eventData (Events.GamepadDirectionChange index) eventTrace Default.Game true world
                | SDL.SDL_EventType.SDL_JOYBUTTONDOWN ->
                    let index = evt.jbutton.which
                    let button = int evt.jbutton.button
                    if GamepadState.isSdlButtonSupported button then
                        let eventData = { GamepadButton = GamepadState.toNuButton button; Down = true }
                        let eventTrace = EventTrace.record4 "World" "processInput" "GamepadButtonDown" EventTrace.empty
                        let world = World.publishPlus World.sortSubscriptionsByHierarchy eventData (Events.GamepadButtonDown index) eventTrace Default.Game true world
                        let eventTrace = EventTrace.record4 "World" "processInput" "GamepadButtonChange" EventTrace.empty
                        World.publishPlus World.sortSubscriptionsByHierarchy eventData (Events.GamepadButtonChange index) eventTrace Default.Game true world
                    else world
                | SDL.SDL_EventType.SDL_JOYBUTTONUP ->
                    let index = evt.jbutton.which
                    let button = int evt.jbutton.button
                    if GamepadState.isSdlButtonSupported button then
                        let eventData = { GamepadButton = GamepadState.toNuButton button; Down = true }
                        let eventTrace = EventTrace.record4 "World" "processInput" "GamepadButtonUp" EventTrace.empty
                        let world = World.publishPlus World.sortSubscriptionsByHierarchy eventData (Events.GamepadButtonUp index) eventTrace Default.Game true world
                        let eventTrace = EventTrace.record4 "World" "processInput" "GamepadButtonChange" EventTrace.empty
                        World.publishPlus World.sortSubscriptionsByHierarchy eventData (Events.GamepadButtonChange index) eventTrace Default.Game true world
                    else world
                | _ -> world
            (World.getLiveness world, world)

        static member private getEntities3 getElementsFromTree world =
            let entityTree = World.getEntityTree world
            let (spatialTree, entityTree) = MutantCache.getMutant (fun () -> World.rebuildEntityTree world) entityTree
            let world = World.setEntityTree entityTree world
            let entities : Entity HashSet = getElementsFromTree spatialTree
            (entities, world)

        [<FunctionBinding>]
        static member getEntitiesInView2 world =
            let viewBounds = World.getViewBoundsRelative world
            World.getEntities3 (SpatialTree.getElementsInBounds viewBounds) world

        [<FunctionBinding>]
        static member getEntitiesInBounds3 bounds world =
            World.getEntities3 (SpatialTree.getElementsInBounds bounds) world

        [<FunctionBinding>]
        static member getEntitiesAtPoint3 point world =
            World.getEntities3 (SpatialTree.getElementsAtPoint point) world

        [<FunctionBinding>]
        static member getEntitiesInView world =
            let entities = HashSet<Entity> HashIdentity.Structural
            let world =
                let (entities2, world) = World.getEntitiesInView2 world
                entities.UnionWith entities2
                world
            (entities, world)

        [<FunctionBinding>]
        static member getEntitiesInBounds bounds world =
            let entities = HashSet<Entity> HashIdentity.Structural
            let world =
                let (entities2, world) = World.getEntitiesInBounds3 bounds world
                entities.UnionWith entities2
                world
            (entities, world)

        [<FunctionBinding>]
        static member getEntitiesAtPoint point world =
            let entities = HashSet<Entity> HashIdentity.Structural
            let world =
                let (entities2, world) = World.getEntitiesAtPoint3 point world
                entities.UnionWith entities2
                world
            (entities, world)

        static member private updateScreenTransition world =
            match World.getSelectedScreenOpt world with
            | Some selectedScreen -> World.updateScreenTransition2 selectedScreen world
            | None -> world

        static member private updateSimulants world =

            // gather simulants
            let screens = match World.getOmniScreenOpt world with Some omniScreen -> [omniScreen] | None -> []
            let screens = match World.getSelectedScreenOpt world with Some selectedScreen -> selectedScreen :: screens | None -> screens
            let screens = List.rev screens
            let layers = Seq.concat (List.map (flip World.getLayers world) screens)
            let (entities, world) = World.getEntitiesInView2 world

            // update simulants breadth-first
            let world = World.updateGame world
            let world = List.fold (fun world screen -> World.updateScreen screen world) world screens
            let world = Seq.fold (fun world layer -> World.updateLayer layer world) world layers
            let world =
                Seq.fold (fun world (entity : Entity) ->
                    if World.isTicking world || entity.GetAlwaysUpdate world
                    then World.updateEntity entity world
                    else world)
                    world
                    entities

            // post-update simulants breadth-first
            let world = World.postUpdateGame world
            let world = List.fold (fun world screen -> World.postUpdateScreen screen world) world screens
            let world = Seq.fold (fun world layer -> World.postUpdateLayer layer world) world layers
            let world =
                Seq.fold (fun world (entity : Entity) ->
                    if World.isTicking world || entity.GetAlwaysUpdate world
                    then World.postUpdateEntity entity world
                    else world)
                    world
                    entities

            // fin
            world

        static member private actualizeScreenTransition5 (_ : Vector2) (eyeSize : Vector2) (screen : Screen) transition world =
            match transition.DissolveImageOpt with
            | Some dissolveImage ->
                let progress = single (screen.GetTransitionTicks world) / single transition.TransitionLifetime
                let alpha = match transition.TransitionType with Incoming -> 1.0f - progress | Outgoing -> progress
                let color = Vector4 (Vector3.One, alpha)
                let position = -eyeSize * 0.5f // negation for right-handedness
                let size = eyeSize
                World.enqueueRenderMessage
                    (RenderDescriptorsMessage
                        [|LayerableDescriptor
                            { Depth = Single.MaxValue
                              AssetTag = dissolveImage
                              PositionY = position.Y
                              LayeredDescriptor =
                                SpriteDescriptor
                                    { Position = position
                                      Size = size
                                      Rotation = 0.0f
                                      Offset = Vector2.Zero
                                      ViewType = Absolute
                                      InsetOpt = None
                                      Image = dissolveImage
                                      Color = color
                                      Flip = FlipNone }}|])
                    world
            | None -> world

        static member private actualizeScreenTransition (screen : Screen) world =
            match screen.GetTransitionState world with
            | IncomingState -> World.actualizeScreenTransition5 (World.getEyeCenter world) (World.getEyeSize world) screen (screen.GetIncoming world) world
            | OutgoingState -> World.actualizeScreenTransition5 (World.getEyeCenter world) (World.getEyeSize world) screen (screen.GetOutgoing world) world
            | IdlingState -> world

        static member private actualizeSimulants world =

            // gather simulants
            let screens = match World.getOmniScreenOpt world with Some omniScreen -> [omniScreen] | None -> []
            let screens = match World.getSelectedScreenOpt world with Some selectedScreen -> selectedScreen :: screens | None -> screens
            let screens = List.rev screens
            let layers = Seq.concat (List.map (flip World.getLayers world) screens)
            let (entities, world) = World.getEntitiesInView2 world

            // actualize simulants breadth-first
            let world = World.actualizeGame world
            let world = List.fold (fun world screen -> World.actualizeScreen screen world) world screens
            let world = match World.getSelectedScreenOpt world with Some selectedScreen -> World.actualizeScreenTransition selectedScreen world | None -> world
            let world = Seq.fold (fun world layer -> World.actualizeLayer layer world) world layers
            let world = Seq.fold (fun world (entity : Entity) -> World.actualizeEntity entity world) world entities

            // fin
            world

        static member private processInput world =
            if SDL.SDL_WasInit SDL.SDL_INIT_TIMER <> 0u then
                let mutable result = (Running, world)
                let polledEvent = ref (SDL.SDL_Event ())
                while
                    SDL.SDL_PollEvent polledEvent <> 0 &&
                    (match fst result with Running -> true | Exiting -> false) do
                    result <- World.processInput2 !polledEvent (snd result)
                result
            else (Exiting, world)

        static member private processPhysics world =
            let physicsEngine = World.getPhysicsEngine world
            let (physicsMessages, physicsEngine) = Subsystem.popMessages physicsEngine
            let world = World.setPhysicsEngine physicsEngine world
            let physicsResult = Subsystem.processMessages physicsMessages physicsEngine world
            Subsystem.applyResult physicsResult (World.getPhysicsEngine world) world

        static member private render renderMessages renderContext renderer world =
            match Constants.Render.ScreenClearing with
            | NoClear -> ()
            | ColorClear (r, g, b) ->
                SDL.SDL_SetRenderDrawColor (renderContext, r, g, b, 255uy) |> ignore
                SDL.SDL_RenderClear renderContext |> ignore
            let result = Subsystem.processMessages renderMessages renderer world
            SDL.SDL_RenderPresent renderContext
            result

        static member private play audioMessages audioPlayer world =
            Subsystem.processMessages audioMessages audioPlayer world

        static member private cleanUp world =
            let world = World.unregisterGame world
            World.cleanUpSubsystems world |> ignore

        /// Run the game engine with the given handlers, but don't clean up at the end, and return the world.
        static member runWithoutCleanUp runWhile preProcess postProcess sdlDeps liveness rendererThreadOpt audioPlayerThreadOpt world =
            if runWhile world then
                let world = preProcess world
                let world = World.preFrame world
                match liveness with
                | Running ->
                    let world = World.updateScreenTransition world
                    match World.getLiveness world with
                    | Running ->
                        let (liveness, world) = World.processInput world
                        match liveness with
                        | Running ->
                            let world = World.processPhysics world
                            match World.getLiveness world with
                            | Running ->
                                let world = World.updateSimulants world
                                match World.getLiveness world with
                                | Running ->
                                    let world = World.processTasklets world
                                    match World.getLiveness world with
                                    | Running ->
                                        let world = World.actualizeSimulants world
                                        match World.getLiveness world with
                                        | Running ->
                                            let world = World.perFrame world
                                            match World.getLiveness world with
                                            | Running ->
#if MULTITHREAD
                                                // attempt to finish renderer thread
                                                let world =
                                                    match rendererThreadOpt with
                                                    | Some rendererThread ->
                                                        let rendererResult = Async.AwaitTask rendererThread |> Async.RunSynchronously
                                                        Subsystem.applyResult rendererResult (World.getRenderer world) world
                                                    | None -> world

                                                // attempt to finish audio player thread
                                                let world =
                                                    match audioPlayerThreadOpt with
                                                    | Some audioPlayerThread->
                                                        let audioPlayerResult = Async.AwaitTask audioPlayerThread |> Async.RunSynchronously
                                                        Subsystem.applyResult audioPlayerResult (World.getAudioPlayer world) world
                                                    | None -> world

                                                // attempt to start renderer thread
                                                let (rendererThreadOpt, world) =
                                                    match SdlDeps.getRenderContextOpt sdlDeps with
                                                    | Some renderContext ->
                                                        let renderer = World.getRenderer world
                                                        let (renderMessages, renderer) = Subsystem.popMessages renderer
                                                        let world = World.setRenderer renderer world
                                                        let rendererThread = Task.Factory.StartNew (fun () -> World.render renderMessages renderContext renderer world)
                                                        (Some rendererThread, world)
                                                    | None -> (None, world)

                                                // attempt to start audio player thread
                                                let (audioPlayerThreadOpt, world) =
                                                    if SDL.SDL_WasInit SDL.SDL_INIT_AUDIO <> 0u then
                                                        let audioPlayer = World.getAudioPlayer world
                                                        let (audioMessages, audioPlayer) = Subsystem.popMessages audioPlayer
                                                        let world = World.setAudioPlayer audioPlayer world
                                                        let audioPlayerThread = Task.Factory.StartNew (fun () -> World.play audioMessages audioPlayer world)
                                                        (Some audioPlayerThread, world)
                                                    else (None, world)
#else
                                                // process rendering on main thread
                                                let world =
                                                    match SdlDeps.getRenderContextOpt sdlDeps with
                                                    | Some renderContext ->
                                                        let renderer = World.getRenderer world
                                                        let (renderMessages, renderer) = Subsystem.popMessages renderer
                                                        let world = World.setRenderer renderer world
                                                        let rendererResult = World.render renderMessages renderContext renderer world
                                                        Subsystem.applyResult rendererResult (World.getRenderer world) world
                                                    | None -> world

                                                // process audio on main thread
                                                let world =
                                                    if SDL.SDL_WasInit SDL.SDL_INIT_AUDIO <> 0u then
                                                        let audioPlayer = World.getAudioPlayer world
                                                        let (audioMessages, audioPlayer) = Subsystem.popMessages audioPlayer
                                                        let world = World.setAudioPlayer audioPlayer world
                                                        let audioPlayerResult = World.play audioMessages audioPlayer world
                                                        Subsystem.applyResult audioPlayerResult (World.getAudioPlayer world) world
                                                    else world
#endif
                                                // post-process the world
                                                let world = World.postFrame world
                                                let world = postProcess world
                                                match World.getLiveness world with
                                                | Running ->

                                                    // update counters and recur
                                                    let world = World.updateTickTime world
                                                    let world = World.incrementUpdateCount world
                                                    World.runWithoutCleanUp runWhile preProcess postProcess sdlDeps liveness rendererThreadOpt audioPlayerThreadOpt world

                                                | Exiting -> world
                                            | Exiting -> world
                                        | Exiting -> world
                                    | Exiting -> world
                                | Exiting -> world
                            | Exiting -> world
                        | Exiting -> world
                    | Exiting -> world
                | Exiting -> world
            else world

        /// Run the game engine with the given handlers.
        static member run4 runWhile sdlDeps liveness world =
            let result =
                try let world = World.runWithoutCleanUp runWhile id id sdlDeps liveness None None world
                    World.cleanUp world
                    Constants.Engine.SuccessExitCode
                with exn ->
                    let world = World.choose world
                    Log.trace (scstring exn)
                    World.cleanUp world
                    Constants.Engine.FailureExitCode
#if MULTITHREAD
            // stops background threads
            Environment.Exit result
#endif
            result

        /// Run the game engine with the given handlers.
        static member run handleAttemptMakeWorld worldConfig =
            match SdlDeps.attemptMake worldConfig.SdlConfig with
            | Right sdlDeps ->
                use sdlDeps = sdlDeps // bind explicitly to dispose automatically
                match handleAttemptMakeWorld sdlDeps worldConfig with
                | Right world -> World.run4 tautology sdlDeps Running world
                | Left error -> Log.trace error; Constants.Engine.FailureExitCode
            | Left error -> Log.trace error; Constants.Engine.FailureExitCode

[<AutoOpen>]
module GameDispatcherModule =

    type World with

        static member internal signalGame<'model, 'message, 'command> signal (game : Game) world =
            match game.GetDispatcher world with
            | :? GameDispatcher<'model, 'message, 'command> as dispatcher ->
                Signal.processSignal signal dispatcher.Message dispatcher.Command (game.Model<'model> ()) game world
            | _ ->
                Log.info "Failed to send signal to game."
                world

        /// Send a signal to a simulant.
        static member signal<'model, 'message, 'command> signal (simulant : Simulant) world =
            match simulant with
            | :? Game as game -> World.signalGame<'model, 'message, 'command> signal game world
            | :? Screen as screen -> World.signalScreen<'model, 'message, 'command> signal screen world
            | :? Layer as layer -> World.signalLayer<'model, 'message, 'command> signal layer world
            | :? Entity as entity -> World.signalEntity<'model, 'message, 'command> signal entity world
            | _ -> failwithumf ()

    and Game with
    
        member this.GetModel<'model> world =
            let property = this.Get<DesignerProperty> Property? Model world
            property.DesignerValue :?> 'model

        member this.SetModel<'model> (value : 'model) world =
            let model = this.Get<DesignerProperty> Property? Model world
            this.Set<DesignerProperty> Property? Model { model with DesignerValue = value } world

        member this.UpdateModel<'model> updater world =
            this.SetModel<'model> (updater this.GetModel<'model> world) world

        member this.Model<'model> () =
            Lens.make<'model, World> Property? Model this.GetModel<'model> this.SetModel<'model> this

        member this.Signal<'model, 'message, 'command> signal world =
            World.signalGame<'model, 'message, 'command> signal this world

    and [<AbstractClass>] GameDispatcher<'model, 'message, 'command> (initial : 'model) =
        inherit GameDispatcher ()

        member this.GetModel (game : Game) world : 'model =
            game.GetModel<'model> world

        member this.SetModel (model : 'model) (game : Game) world =
            game.SetModel<'model> model world

        member this.Model (game : Game) =
            Lens.make Property? Model (this.GetModel game) (flip this.SetModel game) game

        override this.Register (game, world) =
            let (model, world) = World.attachModel initial Property? Model game world
            let bindings = this.Bindings (model, game, world)
            let world = Signal.processBindings bindings this.Message this.Command (this.Model game) game world
            let content = this.Content (this.Model game, game, world)
            List.foldi (fun contentIndex world content ->
                let (screen, world) = World.expandScreenContent World.setScreenSplash content game world
                if contentIndex = 0 then World.selectScreen screen world else world)
                world content

        override this.Actualize (game, world) =
            let views = this.View (this.GetModel game world, game, world)
            World.actualizeViews views world

        abstract member Bindings : 'model * Game * World -> Binding<'message, 'command, Game, World> list
        default this.Bindings (_, _, _) = []

        abstract member Message : 'message * 'model * Game * World -> 'model * Signal<'message, 'command>
        default this.Message (_, model, _, _) = just model

        abstract member Command : 'command * 'model * Game * World -> World * Signal<'message, 'command>
        default this.Command (_, _, _, world) = just world

        abstract member Content : Lens<'model, World> * Game * World -> ScreenContent list
        default this.Content (_, _, _) = []

        abstract member View : 'model * Game * World -> View list
        default this.View (_, _, _) = []