﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2018.

namespace Nu
open System
open System.Collections.Generic
open System.Diagnostics
open System.Reflection
open Prime
open Nu

[<AutoOpen; ModuleBinding>]
module WorldModule =

    /// F# reach-around for evaluating a script expression.
    let mutable internal eval : Scripting.Expr -> Scripting.DeclarationFrame -> Simulant -> World -> struct (Scripting.Expr * World) =
        Unchecked.defaultof<_>

    /// F# reach-around for evaluating script expressions.
    let mutable internal evalMany : Scripting.Expr array -> Scripting.DeclarationFrame -> Simulant -> World -> struct (Scripting.Expr array * World) =
        Unchecked.defaultof<_>

    /// F# reach-around for evaluating a script expression.
    let mutable internal evalWithLogging : Scripting.Expr -> Scripting.DeclarationFrame -> Simulant -> World -> struct (Scripting.Expr * World) =
        Unchecked.defaultof<_>

    /// F# reach-around for evaluating script expressions.
    let mutable internal evalManyWithLogging : Scripting.Expr array -> Scripting.DeclarationFrame -> Simulant -> World -> struct (Scripting.Expr array * World) =
        Unchecked.defaultof<_>

    /// F# reach-around for checking that a simulant is selected.
    let mutable internal isSimulantSelected : Simulant -> World -> bool =
        Unchecked.defaultof<_>

    /// F# reach-around for registering physics entities of an entire screen.
    let mutable internal evictScreenElements : Screen -> World -> World =
        Unchecked.defaultof<_>

    /// F# reach-around for unregistering physics entities of an entire screen.
    let mutable internal admitScreenElements : Screen -> World -> World =
        Unchecked.defaultof<_>

    /// F# reach-around for registering physics entities of an entire screen.
    let mutable internal registerScreenPhysics : Screen -> World -> World =
        Unchecked.defaultof<_>

    /// F# reach-around for unregistering physics entities of an entire screen.
    let mutable internal unregisterScreenPhysics : Screen -> World -> World =
        Unchecked.defaultof<_>

    /// F# reach-around for adding script unsubscriptions to simulants.
    let mutable internal addSimulantScriptUnsubscription : Unsubscription -> Simulant -> World -> World =
        Unchecked.defaultof<_>

    /// F# reach-around for unsubscribing script subscriptions of simulants.
    let mutable internal unsubscribeSimulantScripts : Simulant -> World -> World =
        Unchecked.defaultof<_>

    let mutable internal equate5 : string -> Participant -> World Lens -> bool -> World -> World =
        Unchecked.defaultof<_>

    type World with // Construction

        /// Choose a world to be used for debugging. Call this whenever the most recently constructed
        /// world value is to be discarded in favor of the given world value.
        static member choose (world : World) =
#if DEBUG
            Debug.World.Chosen <- world :> obj
#endif
            world

        static member assertChosen (world : World) =
#if DEBUG
            if world :> obj <> Debug.World.Chosen then
                Console.WriteLine "Fault"
#endif
            ignore world

        /// Make the world.
        static member internal make plugin eventDelegate dispatchers subsystems scriptingEnv ambientState spatialTree activeGameDispatcher =
            let gameState = GameState.make activeGameDispatcher
            let screenStates = UMap.makeEmpty Constants.Engine.SimulantMapConfig
            let layerStates = UMap.makeEmpty Constants.Engine.SimulantMapConfig
            let entityStates = UMap.makeEmpty Constants.Engine.SimulantMapConfig
            let world =
                World.choose
                    { EventSystemDelegate = eventDelegate
                      EntityCachedOpt = KeyedCache.make (KeyValuePair (Address.empty<Entity>, entityStates)) None
                      EntityTree = MutantCache.make id spatialTree
                      EntityStates = entityStates
                      LayerStates = layerStates
                      ScreenStates = screenStates
                      GameState = gameState
                      AmbientState = ambientState
                      Subsystems = subsystems
                      ScreenDirectory = UMap.makeEmpty Constants.Engine.SimulantMapConfig
                      Dispatchers = dispatchers
                      ScriptingEnv = scriptingEnv
                      ScriptingContext = Game ()
                      Plugin = plugin }
            let world =
                World.choose
                    { world with GameState = Reflection.attachProperties GameState.copy gameState.Dispatcher gameState world }
            world

    type World with // EventSystem

        /// Get event subscriptions.
        static member internal getSubscriptions world =
            EventSystem.getSubscriptions<World> world

        /// Get event unsubscriptions.
        static member internal getUnsubscriptions world =
            EventSystem.getUnsubscriptions<World> world

        /// Get whether events are being traced.
        static member getEventTracing (world : World) =
            EventSystem.getEventTracing world

        /// Set whether events are being traced.
        static member setEventTracing tracing (world : World) =
            World.choose (EventSystem.setEventTracing tracing world)

        /// Get the state of the event filter.
        static member getEventFilter (world : World) =
            EventSystem.getEventFilter world

        /// Set the state of the event filter.
        static member setEventFilter filter (world : World) =
            World.choose (EventSystem.setEventFilter filter world)

        /// Get the context of the event system.
        static member getEventContext (world : World) =
            EventSystem.getEventContext world

        /// A hack to allow a sidelined event system to be continued by resetting its mutable state.
        static member internal continueEventSystemHack (world : World) =
            EventSystem.setEventContext (Game ()) world

        /// Set the context of the event system.
#if DEBUG
        static member internal withEventContext operation (context : Participant) (world : World) =
            let oldContext = World.getEventContext world
            EventSystem.setEventContext context world
            let world = operation world
            EventSystem.setEventContext oldContext world
            world
#else
        static member inline internal withEventContext operation _ (world : World) =
            // NOTE: inlined to hopefully get rid of the lambda
            operation world
#endif

        /// Qualify the context of the event system.
        static member internal qualifyEventContext address (world : World) =
            EventSystem.qualifyEventContext address world

        /// Sort subscriptions using categorization via the 'by' procedure.
        static member sortSubscriptionsBy by subscriptions (world : World) =
            EventSystem.sortSubscriptionsBy by subscriptions world

        /// Sort subscriptions by their place in the world's simulant hierarchy.
        static member sortSubscriptionsByHierarchy subscriptions (world : World) =
            // OPTIMIZATION: entity priority boxed up front to decrease GC pressure.
            let entityPriorityBoxed = Constants.Engine.EntitySortPriority :> IComparable
            World.sortSubscriptionsBy
                (fun (participant : Participant) _ ->
                    match participant with
                    | :? GlobalParticipantGeneralized
                    | :? Game -> Constants.Engine.GameSortPriority :> IComparable
                    | :? Screen -> Constants.Engine.ScreenSortPriority :> IComparable
                    | :? Layer -> Constants.Engine.LayerSortPriority :> IComparable
                    | :? Entity -> entityPriorityBoxed
                    | _ -> failwithumf ())
                subscriptions
                world

        /// A 'no-op' for subscription sorting - that is, performs no sorting at all.
        static member sortSubscriptionsNone subscriptions (world : World) =
            EventSystem.sortSubscriptionsNone subscriptions world

        /// Publish an event, using the given getSubscriptions and publishSorter procedures to arrange the order to which subscriptions are published.
        static member publishPlus<'a, 'p when 'p :> Simulant> publishSorter (eventData : 'a) (eventAddress : 'a Address) eventTrace (publisher : 'p) allowWildcard world =
            World.choose (EventSystem.publishPlus<'a, 'p, World> publishSorter eventData eventAddress eventTrace publisher allowWildcard world)

        /// Publish an event.
        static member publish<'a, 'p when 'p :> Simulant>
            (eventData : 'a) (eventAddress : 'a Address) eventTrace (publisher : 'p) world =
            World.choose (EventSystem.publishPlus<'a, 'p, World> World.sortSubscriptionsByHierarchy eventData eventAddress eventTrace publisher true world)

        /// Unsubscribe from an event.
        static member unsubscribe subscriptionKey world =
            World.choose (EventSystem.unsubscribe<World> subscriptionKey world)

        /// Subscribe to an event using the given subscriptionKey, and be provided with an unsubscription callback.
        static member subscribePlus<'a, 's when 's :> Participant>
            subscriptionKey (subscription : Event<'a, 's> -> World -> Handling * World) (eventAddress : 'a Address) (subscriber : 's) world =
            mapSnd World.choose (EventSystem.subscribePlus<'a, 's, World> subscriptionKey subscription eventAddress subscriber world)

        /// Subscribe to an event.
        static member subscribe<'a, 's when 's :> Participant>
            (subscription : Event<'a, 's> -> World -> World) (eventAddress : 'a Address) (subscriber : 's) world =
            World.choose (EventSystem.subscribe<'a, 's, World> subscription eventAddress subscriber world)

        /// Keep active a subscription for the lifetime of a simulant, and be provided with an unsubscription callback.
        static member monitorPlus<'a, 's when 's :> Participant>
            (subscription : Event<'a, 's> -> World -> Handling * World) (eventAddress : 'a Address) (subscriber : 's) world =
            mapSnd World.choose (EventSystem.monitorPlus<'a, 's, World> subscription eventAddress subscriber world)

        /// Keep active a subscription for the lifetime of a simulant.
        static member monitor<'a, 's when 's :> Participant>
            (subscription : Event<'a, 's> -> World -> World) (eventAddress : 'a Address) (subscriber : 's) world =
            World.choose (EventSystem.monitor<'a, 's, World> subscription eventAddress subscriber world)

    type World with // Caching

        /// Get the optional cached entity.
        static member internal getEntityCachedOpt world =
            world.EntityCachedOpt

        /// Get the screen directory.
        static member internal getScreenDirectory world =
            world.ScreenDirectory

    type World with // EntityTree

        static member internal getEntityTree world =
            world.EntityTree

        static member internal setEntityTree entityTree world =
            World.choose { world with EntityTree = entityTree }

        static member internal updateEntityTree updater world =
            World.setEntityTree (updater (World.getEntityTree world))

    type World with // Dispatchers

        /// Get the game dispatchers of the world.
        static member getGameDispatchers world =
            world.Dispatchers.GameDispatchers

        /// Get the screen dispatchers of the world.
        static member getScreenDispatchers world =
            world.Dispatchers.ScreenDispatchers

        /// Get the layer dispatchers of the world.
        static member getLayerDispatchers world =
            world.Dispatchers.LayerDispatchers

        /// Get the entity dispatchers of the world.
        static member getEntityDispatchers world =
            world.Dispatchers.EntityDispatchers

        /// Get the facets of the world.
        static member getFacets world =
            world.Dispatchers.Facets

    type World with // AmbientState

        static member internal getAmbientState world =
            world.AmbientState

        static member internal getAmbientStateBy by world =
            by world.AmbientState

        static member internal updateAmbientState updater world =
            World.choose { world with AmbientState = updater world.AmbientState }

        static member internal updateTickTime world =
            World.updateAmbientState AmbientState.updateTickTime world

        static member internal incrementUpdateCount world =
            World.updateAmbientState AmbientState.incrementUpdateCount world
    
        /// Get the tick rate.
        [<FunctionBinding>]
        static member getTickRate world =
            World.getAmbientStateBy AmbientState.getTickRate world

        /// Get the tick rate as a floating-point value.
        [<FunctionBinding>]
        static member getTickRateF world =
            World.getAmbientStateBy AmbientState.getTickRateF world

        /// Set the tick rate, starting at the end of the current frame.
        [<FunctionBinding>]
        static member setTickRate tickRate world =
            World.schedule2 (World.updateAmbientState (AmbientState.setTickRateImmediate tickRate)) world

        /// Reset the tick time to 0 at the end of the current frame.
        [<FunctionBinding>]
        static member resetTickTime world =
            World.schedule2 (World.updateAmbientState AmbientState.resetTickTimeImmediate) world

        /// Increment the tick time at the end of the current frame.
        [<FunctionBinding>]
        static member incTickTime world =
            World.schedule2 (World.updateAmbientState AmbientState.incTickTimeImmediate) world

        /// Decrement the tick time at the end of the current frame.
        [<FunctionBinding>]
        static member decTickTime world =
            World.schedule2 (World.updateAmbientState AmbientState.decTickTimeImmediate) world

        /// Get the world's tick time.
        [<FunctionBinding>]
        static member getTickTime world =
            World.getAmbientStateBy AmbientState.getTickTime world

        /// Check that the world is ticking.
        [<FunctionBinding>]
        static member isTicking world =
            World.getAmbientStateBy AmbientState.isTicking world

        /// Get the world's update count.
        [<FunctionBinding>]
        static member getUpdateCount world =
            World.getAmbientStateBy AmbientState.getUpdateCount world

        /// Get the the liveness state of the engine.
        [<FunctionBinding>]
        static member getLiveness world =
            World.getAmbientStateBy AmbientState.getLiveness world

        /// Place the engine into a state such that the app will exit at the end of the current frame.
        [<FunctionBinding>]
        static member exit world =
            World.updateAmbientState AmbientState.exit world

        static member internal getMetadata world =
            AmbientState.getMetadata (World.getAmbientState world)

        static member internal setMetadata assetMetadataMap world =
            World.updateAmbientState (AmbientState.setMetadata assetMetadataMap) world

        /// Try to get the texture metadata of the given asset.
        [<FunctionBinding>]
        static member tryGetTextureSize assetTag world =
            Metadata.tryGetTextureSize assetTag (World.getMetadata world)

        /// Forcibly get the texture size metadata of the given asset (throwing on failure).
        [<FunctionBinding>]
        static member getTextureSize assetTag world =
            Metadata.getTextureSize assetTag (World.getMetadata world)

        /// Try to get the texture size metadata of the given asset.
        [<FunctionBinding>]
        static member tryGetTextureSizeF assetTag world =
            Metadata.tryGetTextureSizeF assetTag (World.getMetadata world)

        /// Forcibly get the texture size metadata of the given asset (throwing on failure).
        [<FunctionBinding>]
        static member getTextureSizeF assetTag world =
            Metadata.getTextureSizeF assetTag (World.getMetadata world)

        /// Try to get the tile map metadata of the given asset.
        static member tryGetTileMapMetadata assetTag world =
            Metadata.tryGetTileMapMetadata assetTag (World.getMetadata world)

        /// Forcibly get the tile map metadata of the given asset (throwing on failure).
        static member getTileMapMetadata assetTag world =
            Metadata.getTileMapMetadata assetTag (World.getMetadata world)

        /// Get a copy of the metadata map.
        static member getMetadataMap world =
            Metadata.getMetadataMap (World.getMetadata world)

        /// Get a map of all discovered assets.
        static member getAssetMap world =
            Metadata.getAssetMap (World.getMetadata world)

        static member internal getKeyValueStoreBy by world =
            World.getAmbientStateBy (AmbientState.getKeyValueStoreBy by) world

        static member internal getKeyValueStore world =
            World.getAmbientStateBy AmbientState.getKeyValueStore world

        static member internal setKeyValueStore symbolStore world =
            World.updateAmbientState (AmbientState.setKeyValueStore symbolStore) world

        static member internal updateKeyValueStore updater world =
            World.updateAmbientState (AmbientState.updateKeyValueStore updater) world

        static member tryGetKeyedValue<'a> key world =
            match World.getKeyValueStoreBy (UMap.tryFind key) world with
            | Some value -> Some (value :?> 'a)
            | None -> None

        static member getKeyedValue<'a> key world =
            World.getKeyValueStoreBy (UMap.find key) world :?> 'a

        static member addKeyedValue<'a> key (value : 'a) world =
            World.updateKeyValueStore (UMap.add key (value :> obj)) world

        static member removeKeyedValue key world =
            World.updateKeyValueStore (UMap.remove key) world

        static member updateKeyedValue<'a> (updater : 'a -> 'a) key world =
            World.addKeyedValue key (updater (World.getKeyedValue<'a> key world)) world

        static member internal getTasklets world =
            World.getAmbientStateBy AmbientState.getTasklets world

        static member internal clearTasklets world =
            World.updateAmbientState AmbientState.clearTasklets world

        static member internal restoreTasklets tasklets world =
            World.updateAmbientState (AmbientState.restoreTasklets tasklets) world

        static member internal getTaskletsProcessing world =
            World.getAmbientStateBy AmbientState.getTaskletsProcessing world

        /// Add a tasklet to be executed by the engine at the scheduled time.
        static member addTasklet tasklet world =
            World.updateAmbientState (AmbientState.addTasklet tasklet) world

        /// Add multiple tasklets to be executed by the engine at the scheduled times.
        static member addTasklets tasklets world =
            World.updateAmbientState (AmbientState.addTasklets tasklets) world

        /// Schedule an operation to be executed by the engine at the given time.
        static member schedule fn time world =
            let tasklet = { ScheduledTime = time; Command = { Execute = fn }}
            World.addTasklet tasklet world

        /// Schedule an operation to be executed by the engine at the end of the current frame.
        static member schedule2 fn world =
            let taskletsProcessing = World.getTaskletsProcessing world
            World.schedule fn (World.getTickTime world + if taskletsProcessing then 1L else 0L) world

        /// Attempt to get the window flags.
        static member tryGetWindowFlags world =
            World.getAmbientStateBy AmbientState.tryGetWindowFlags world

        /// Attempt to check that the window is minimized.
        static member tryGetWindowMinimized world =
            World.getAmbientStateBy AmbientState.tryGetWindowMinimized world

        /// Attempt to check that the window is maximized.
        static member tryGetWindowMaximized world =
            World.getAmbientStateBy AmbientState.tryGetWindowMaximized world

        static member internal getSymbolStoreBy by world =
            World.getAmbientStateBy (AmbientState.getSymbolStoreBy by) world

        static member internal getSymbolStore world =
            World.getAmbientStateBy AmbientState.getSymbolStore world

        static member internal setSymbolStore symbolStore world =
            World.updateAmbientState (AmbientState.setSymbolStore symbolStore) world

        static member internal updateSymbolStore updater world =
            World.updateAmbientState (AmbientState.updateSymbolStore updater) world

        /// Try to load a symbol store package with the given name.
        static member tryLoadSymbolStorePackage implicitDelimiters packageName world =
            World.getSymbolStoreBy (SymbolStore.tryLoadSymbolPackage implicitDelimiters packageName) world

        /// Unload a symbol store package with the given name.
        static member unloadSymbolStorePackage packageName world =
            World.getSymbolStoreBy (SymbolStore.unloadSymbolPackage packageName) world

        /// Try to find a symbol with the given asset tag.
        static member tryFindSymbol assetTag metadata world =
            let symbolStore = World.getSymbolStore world
            SymbolStore.tryFindSymbol assetTag metadata symbolStore

        /// Try to find symbols with the given asset tags.
        static member tryFindSymbols implicitDelimiters assetTags world =
            let symbolStore = World.getSymbolStore world
            SymbolStore.tryFindSymbols implicitDelimiters assetTags symbolStore

        /// Reload all the symbols in the symbol store.
        [<FunctionBinding>]
        static member reloadSymbols world =
            World.getSymbolStoreBy SymbolStore.reloadSymbols world

        static member internal getOverlayerBy by world =
            let overlayer = World.getAmbientStateBy AmbientState.getOverlayer world
            by overlayer

        static member internal getOverlayer world =
            World.getOverlayerBy id world

        static member internal setOverlayer overlayer world =
            World.updateAmbientState (AmbientState.setOverlayer overlayer) world

        /// Get intrinsic overlays.
        static member getIntrinsicOverlays world =
            World.getOverlayerBy Overlayer.getIntrinsicOverlays world

        /// Get extrinsic overlays.
        static member getExtrinsicOverlays world =
            World.getOverlayerBy Overlayer.getExtrinsicOverlays world

        static member internal getOverlayRouter world =
            World.getAmbientStateBy AmbientState.getOverlayRouter world

        static member internal getOverlayRouterBy by world =
            let overlayRouter = World.getOverlayRouter world
            by overlayRouter

        static member internal tryFindRoutedOverlayNameOpt dispatcherName state =
            World.getOverlayRouterBy (OverlayRouter.tryFindOverlayNameOpt dispatcherName) state

        /// Make vanilla overlay routes from dispatchers.
        static member internal dispatchersToOverlayRoutes entityDispatchers =
            entityDispatchers |>
            Map.toValueListBy getTypeName |>
            List.map (fun typeName -> (typeName, None))

    type World with // Subsystems

        static member internal getSubsystems world =
            world.Subsystems

        static member internal setSubsystems subsystems world =
            World.choose { world with Subsystems = subsystems }

        static member internal updateSubsystems updater world =
            World.setSubsystems (updater world.Subsystems) world

        static member internal cleanUpSubsystems world =
            let subsystems = World.getSubsystems world
            let (subsystems, world) = Subsystems.cleanUp subsystems world
            World.setSubsystems subsystems world

    type World with // Scripting

        static member internal getGlobalFrame (world : World) =
            ScriptingSystem.getGlobalFrame world

        static member internal getLocalFrame (world : World) =
            ScriptingSystem.getLocalFrame world

        static member internal setLocalFrame localFrame (world : World) =
            ScriptingSystem.setLocalFrame localFrame world

        /// Get the context of the script system.
        static member internal getScriptContext (world : World) =
            world.ScriptingContext

        /// Set the context of the script system.
        static member internal setScriptContext context (world : World) =
            World.choose { world with ScriptingContext = context }

        /// Evaluate a script expression.
        static member eval expr localFrame simulant world =
            eval expr localFrame simulant world

        /// Evaluate a script expression, with logging on violation result.
        static member evalWithLogging expr localFrame simulant world =
            evalWithLogging expr localFrame simulant world

        /// Evaluate a series of script expressions.
        static member evalMany exprs localFrame simulant world =
            evalMany exprs localFrame simulant world

        /// Evaluate a series of script expressions, with logging on violation results.
        static member evalManyWithLogging exprs localFrame simulant world =
            evalManyWithLogging exprs localFrame simulant world

        /// Attempt to evaluate a script.
        static member tryEvalScript scriptFilePath world =
            ScriptingSystem.tryEvalScript World.choose scriptFilePath world

    type World with // Plugin

        static member internal preFrame world =
            world.Plugin.PreFrame world

        static member internal perFrame world =
            world.Plugin.PerFrame world

        static member internal postFrame world =
            world.Plugin.PostFrame world

    type World with // Debugging

        /// View the member properties of some SimulantState.
        static member internal getMemberProperties (state : SimulantState) =
            state |>
            getType |>
            getProperties |>
            Array.map (fun (property : PropertyInfo) -> (property.Name, property.PropertyType, property.GetValue state)) |>
            Array.toList

        /// View the xtension properties of some SimulantState.
        static member internal getXtensionProperties (state : SimulantState) =
            state.GetXtension () |>
            Xtension.toSeq |>
            List.ofSeq |>
            List.sortBy fst |>
            List.map (fun (name, property) -> (name, property.PropertyType, property.PropertyValue))

        /// Provides a full view of all the properties of some SimulantState.
        static member internal getProperties state =
            List.append
                (World.getMemberProperties state)
                (World.getXtensionProperties state)

    type World with // Handlers

        /// Ignore all handled events.
        static member handleAsPass<'a, 's when 's :> Simulant> (_ : Event<'a, 's>) (world : World) =
            (Cascade, world)

        /// Swallow all handled events.
        static member handleAsSwallow<'a, 's when 's :> Simulant> (_ : Event<'a, 's>) (world : World) =
            (Resolve, world)
            
        /// Handle event by exiting app.
        static member handleAsExit<'a, 's when 's :> Simulant> (_ : Event<'a, 's>) (world : World) =
            (Resolve, World.exit world)