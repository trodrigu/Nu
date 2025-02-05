﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2018.

namespace Nu
open System
open System.Collections.Generic
open System.Reflection
open SDL2
open Prime
open Nu

[<RequireQualifiedAccess; CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module Nu =

    let mutable private Initialized = false
    let private LoadedAssemblies = Dictionary<string, Assembly> HashIdentity.Structural

    /// Initialize the Nu game engine.
    let init nuConfig =

        // init only if needed
        if not Initialized then

            // make types load reflectively from pathed (non-static) assemblies
            AppDomain.CurrentDomain.AssemblyLoad.Add
                (fun args -> LoadedAssemblies.[args.LoadedAssembly.FullName] <- args.LoadedAssembly)
            AppDomain.CurrentDomain.add_AssemblyResolve (ResolveEventHandler
                (fun _ args -> snd (LoadedAssemblies.TryGetValue args.Name)))

            // ensure the current culture is invariate
            Threading.Thread.CurrentThread.CurrentCulture <- Globalization.CultureInfo.InvariantCulture

            // init logging
            Log.init (Some "Log.txt")

            // init math module
            Math.init ()

            // init simulant modules
            WorldModuleGame.init ()
            WorldModuleScreen.init ()
            WorldModuleLayer.init ()
            WorldModuleEntity.init ()

            // init getPropertyOpt F# reach-around
            WorldTypes.getPropertyOpt <- fun propertyName propertied world ->
                match propertied with
                | :? Simulant as simulant ->
                    match World.tryGetProperty propertyName simulant (world :?> World) with
                    | Some property -> Some property.PropertyValue
                    | None -> None
                | _ -> None

            // init setPropertyOpt F# reach-around
            WorldTypes.setPropertyOpt <- fun propertyName propertied valueOpt ty world ->
                let world = world :?> World
                match propertied with
                | :? Simulant as simulant ->
                    match simulant with
                    | :? Game ->
                        match (valueOpt, WorldModuleGame.Setters.TryGetValue propertyName) with
                        | (Some value, (true, setter)) ->
                            let property = { PropertyValue = value; PropertyType = ty }
                            setter property world |> snd |> box
                        | (Some value, (false, _)) ->
                            let property = { PropertyValue = value; PropertyType = ty }
                            World.attachGameProperty propertyName property world |> box
                        | (None, (true, _)) ->
                            World.exit world |> box
                        | (None, (false, _)) ->
                            World.detachGameProperty propertyName world |> box
                    | :? Screen as screen ->
                        match (valueOpt, WorldModuleScreen.Setters.TryGetValue propertyName) with
                        | (Some value, (true, setter)) ->
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let property = { PropertyValue = value; PropertyType = ty }
                            setter property screen world |> snd |> box
                        | (Some value, (false, _)) ->
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let property = { PropertyValue = value; PropertyType = ty }
                            World.attachScreenProperty propertyName property screen world |> box
                        | (None, (true, _)) ->
                            World.destroyScreen screen world |> box
                        | (None, (false, _)) ->
                            World.detachScreenProperty propertyName screen world |> box
                    | :? Layer as layer ->
                        match (valueOpt, WorldModuleLayer.Setters.TryGetValue propertyName) with
                        | (Some value, (true, setter)) ->
                            let screen = ltos layer
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let world = if not (World.getLayerExists layer world) then World.createLayer None screen world |> snd else world
                            let property = { PropertyValue = value; PropertyType = ty }
                            setter property layer world |> snd |> box
                        | (Some value, (false, _)) ->
                            let screen = ltos layer
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let world = if not (World.getLayerExists layer world) then World.createLayer None screen world |> snd else world
                            let property = { PropertyValue = value; PropertyType = ty }
                            World.attachLayerProperty propertyName property layer world |> box
                        | (None, (true, _)) ->
                            World.destroyLayer layer world |> box
                        | (None, (false, _)) ->
                            World.detachLayerProperty propertyName layer world |> box
                    | :? Entity as entity ->
                        match (valueOpt, WorldModuleEntity.Setters.TryGetValue propertyName) with
                        | (Some value, (true, setter)) ->
                            let layer = etol entity
                            let screen = ltos layer
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let world = if not (World.getLayerExists layer world) then World.createLayer None screen world |> snd else world
                            let world = if not (World.getEntityExists entity world) then World.createEntity None DefaultOverlay layer world |> snd else world
                            let property = { PropertyValue = value; PropertyType = ty }
                            setter property entity world |> snd |> box
                        | (Some value, (false, _)) ->
                            let layer = etol entity
                            let screen = ltos layer
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let world = if not (World.getLayerExists layer world) then World.createLayer None screen world |> snd else world
                            let world = if not (World.getEntityExists entity world) then World.createEntity None DefaultOverlay layer world |> snd else world
                            let alwaysPublish = Reflection.isPropertyAlwaysPublishByName propertyName
                            let nonPersistent = not (Reflection.isPropertyPersistentByName propertyName)
                            let property = { PropertyValue = value; PropertyType = ty }
                            World.attachEntityProperty propertyName alwaysPublish nonPersistent property entity world |> box
                        | (None, (true, _)) ->
                            World.destroyEntity entity world |> box
                        | (None, (false, _)) ->
                            World.detachEntityProperty propertyName entity world |> box
                    | _ -> failwithumf ()
                | _ -> box world

            // init handlePropertyChange F# reach-around
            WorldTypes.handlePropertyChange <- fun propertyName propertied handler world ->
                let participant = propertied :?> Participant
                let handler = handler :?> World PropertyChangeHandler
                let (unsubscribe, world) =
                    World.subscribePlus
                        (makeGuid ())
                        (fun (event : Event<obj, _>) world ->
                            let data = event.Data :?> ChangeData
                            let world = handler data.Value world
                            (Cascade, world))
                        (rtoa (Array.append [|"Change"; propertyName; "Event"|] (Address.getNames participant.ParticipantAddress)))
                        (Default.Game :> Participant)
                        (world :?> World)
                (box unsubscribe, box world)

            // init eval F# reach-around
            // TODO: remove duplicated code with the following 4 functions...
            WorldModule.eval <- fun expr localFrame scriptContext world ->
                match expr with
                | Scripting.Unit ->
                    // OPTIMIZATION: don't bother evaluating unit
                    struct (Scripting.Unit, world)
                | _ ->
                    let oldLocalFrame = World.getLocalFrame world
                    let oldScriptContext = World.getScriptContext world
                    World.setLocalFrame localFrame world
                    let world = World.setScriptContext scriptContext world
                    ScriptingSystem.addProceduralBindings (Scripting.AddToNewFrame 1) (seq { yield struct ("self", Scripting.String (scstring scriptContext)) }) world
                    let struct (evaled, world) = World.evalInternal expr world
                    ScriptingSystem.removeProceduralBindings world
                    let world = World.setScriptContext oldScriptContext world
                    World.setLocalFrame oldLocalFrame world
                    struct (evaled, world)

            // init evalMany F# reach-around
            WorldModule.evalMany <- fun exprs localFrame scriptContext world ->
                let oldLocalFrame = World.getLocalFrame world
                let oldScriptContext = World.getScriptContext world
                World.setLocalFrame localFrame world
                let world = World.setScriptContext scriptContext world
                ScriptingSystem.addProceduralBindings (Scripting.AddToNewFrame 1) (seq { yield struct ("self", Scripting.String (scstring scriptContext)) }) world
                let struct (evaleds, world) = World.evalManyInternal exprs world
                ScriptingSystem.removeProceduralBindings world
                let world = World.setScriptContext oldScriptContext world
                World.setLocalFrame oldLocalFrame world
                struct (evaleds, world)

            // init evalWithLogging F# reach-around
            WorldModule.evalWithLogging <- fun expr localFrame scriptContext world ->
                match expr with
                | Scripting.Unit ->
                    // OPTIMIZATION: don't bother evaluating unit
                    struct (Scripting.Unit, world)
                | _ ->
                    let oldLocalFrame = World.getLocalFrame world
                    let oldScriptContext = World.getScriptContext world
                    World.setLocalFrame localFrame world
                    let world = World.setScriptContext scriptContext world
                    ScriptingSystem.addProceduralBindings (Scripting.AddToNewFrame 1) (seq { yield struct ("self", Scripting.String (scstring scriptContext)) }) world
                    let struct (evaled, world) = World.evalWithLoggingInternal expr world
                    ScriptingSystem.removeProceduralBindings world
                    let world = World.setScriptContext oldScriptContext world
                    World.setLocalFrame oldLocalFrame world
                    struct (evaled, world)

            // init evalMany F# reach-around
            WorldModule.evalManyWithLogging <- fun exprs localFrame scriptContext world ->
                let oldLocalFrame = World.getLocalFrame world
                let oldScriptContext = World.getScriptContext world
                World.setLocalFrame localFrame world
                let world = World.setScriptContext scriptContext world
                ScriptingSystem.addProceduralBindings (Scripting.AddToNewFrame 1) (seq { yield struct ("self", Scripting.String (scstring scriptContext)) }) world
                let struct (evaleds, world) = World.evalManyWithLoggingInternal exprs world
                ScriptingSystem.removeProceduralBindings world
                let world = World.setScriptContext oldScriptContext world
                World.setLocalFrame oldLocalFrame world
                struct (evaleds, world)

            // init isSimulantSelected F# reach-around
            WorldModule.isSimulantSelected <- fun simulant world ->
                World.isSimulantSelected simulant world
                
            // init admitScreenElements F# reach-around
            WorldModule.admitScreenElements <- fun screen world ->
                let entities = World.getLayers screen world |> Seq.map (flip World.getEntities world) |> Seq.concat |> Seq.toArray
                let oldWorld = world
                let entityTree =
                    MutantCache.mutateMutant
                        (fun () -> oldWorld.Dispatchers.RebuildEntityTree oldWorld)
                        (fun entityTree ->
                            for entity in entities do
                                let entityState = World.getEntityState entity world
                                let entityMaxBounds = World.getEntityStateBoundsMax entityState
                                let entityOmnipresent = entityState.Transform.Omnipresent || entityState.Transform.ViewType = Absolute
                                SpatialTree.addElement entityOmnipresent entityMaxBounds entity entityTree
                            entityTree)
                        (World.getEntityTree world)
                World.setEntityTree entityTree world
                
            // init evictScreenElements F# reach-around
            WorldModule.evictScreenElements <- fun screen world ->
                let entities = World.getLayers screen world |> Seq.map (flip World.getEntities world) |> Seq.concat |> Seq.toArray
                let oldWorld = world
                let entityTree =
                    MutantCache.mutateMutant
                        (fun () -> oldWorld.Dispatchers.RebuildEntityTree oldWorld)
                        (fun entityTree ->
                            for entity in entities do
                                let entityState = World.getEntityState entity world
                                let entityMaxBounds = World.getEntityStateBoundsMax entityState
                                let entityOmnipresent = entityState.Transform.Omnipresent || entityState.Transform.ViewType = Absolute
                                SpatialTree.removeElement entityOmnipresent entityMaxBounds entity entityTree
                            entityTree)
                        (World.getEntityTree world)
                World.setEntityTree entityTree world

            // init registerScreenPhysics F# reach-around
            WorldModule.registerScreenPhysics <- fun screen world ->
                let entities = World.getLayers screen world |> Seq.map (flip World.getEntities world) |> Seq.concat |> Seq.toArray
                Array.fold (fun world (entity : Entity) ->
                    World.registerEntityPhysics entity world)
                    world entities

            // init unregisterScreenPhysics F# reach-around
            WorldModule.unregisterScreenPhysics <- fun screen world ->
                let entities = World.getLayers screen world |> Seq.map (flip World.getEntities world) |> Seq.concat |> Seq.toArray
                Array.fold (fun world (entity : Entity) ->
                    World.unregisterEntityPhysics entity world)
                    world entities

            // init addSimulantScriptUnsubscription F# reach-around
            WorldModule.addSimulantScriptUnsubscription <- fun unsubscription (simulant : Simulant) world ->
                match simulant with
                | :? Game as game -> game.ScriptUnsubscriptions.Update (List.cons unsubscription) world
                | :? Screen as screen -> screen.ScriptUnsubscriptions.Update (List.cons unsubscription) world
                | :? Layer as layer -> layer.ScriptUnsubscriptions.Update (List.cons unsubscription) world
                | :? Entity as entity -> entity.ScriptUnsubscriptions.Update (List.cons unsubscription) world
                | _ -> world

            // init unsubscribeSimulantScripts F# reach-around
            WorldModule.unsubscribeSimulantScripts <- fun (simulant : Simulant) world ->
                let propertyOpt =
                    match simulant with
                    | :? Game as game -> Some game.ScriptUnsubscriptions
                    | :? Screen as screen -> Some screen.ScriptUnsubscriptions
                    | :? Layer as layer -> Some layer.ScriptUnsubscriptions
                    | :? Entity as entity -> Some entity.ScriptUnsubscriptions
                    | _ -> None
                match propertyOpt with
                | Some property ->
                    let unsubscriptions = property.Get world
                    let world = List.foldBack apply unsubscriptions world
                    property.Set [] world
                | None -> world

            // init equate5 F# reach-around
            WorldModule.equate5 <- fun name (participant : Participant) (lens : World Lens) breaking world ->
                let nonPersistent = not (Reflection.isPropertyPersistentByName name)
                let alwaysPublish = Reflection.isPropertyAlwaysPublishByName name
                let propagate (_ : Event<obj, Participant>) world =
                    let property = { PropertyType = lens.Type; PropertyValue = lens.Get world }
                    World.setProperty name nonPersistent alwaysPublish property (participant :?> Simulant) world
                let breaker = if breaking then Stream.noMoreThanOncePerUpdate else Stream.id
                let world = Stream.make (atooa Events.Register --> lens.This.ParticipantAddress) |> breaker |> Stream.optimize |> Stream.monitor propagate participant $ world
                Stream.make (atooa (Events.Change lens.Name) --> lens.This.ParticipantAddress) |> breaker |> Stream.optimize |> Stream.monitor propagate participant $ world

            // init scripting
            World.initScripting ()
            WorldBindings.initBindings ()

            // init debug view F# reach-arounds
#if DEBUG
            Debug.World.viewGame <- fun world -> Debug.Game.view (world :?> World)
            Debug.World.viewScreen <- fun screen world -> Debug.Screen.view (screen :?> Screen) (world :?> World)
            Debug.World.viewLayer <- fun layer world -> Debug.Layer.view (layer :?> Layer) (world :?> World)
            Debug.World.viewEntity <- fun entity world -> Debug.Entity.view (entity :?> Entity) (world :?> World)
#endif

            // init Vsync with incoming parameter
            Vsync.init nuConfig.RunSynchronously

            // init event world caching
            EventSystem.setEventAddressCaching true

            // mark init flag
            Initialized <- true

[<AutoOpen; ModuleBinding>]
module WorldModule3 =

    type World with

        static member private pairWithName source =
            (getTypeName source, source)

        static member private makeDefaultGameDispatcher () =
            World.pairWithName (GameDispatcher ())
            
        static member private makeDefaultScreenDispatchers () =
            Map.ofList [World.pairWithName (ScreenDispatcher ())]

        static member private makeDefaultLayerDispatchers () =
            Map.ofList [World.pairWithName (LayerDispatcher ())]

        static member private makeDefaultEntityDispatchers () =
            // TODO: consider if we shoud reflectively generate these
            Map.ofListBy World.pairWithName $
                [EntityDispatcher ()
                 NodeDispatcher () :> EntityDispatcher
                 EffectDispatcher () :> EntityDispatcher
                 GuiDispatcher () :> EntityDispatcher
                 ButtonDispatcher () :> EntityDispatcher
                 LabelDispatcher () :> EntityDispatcher
                 TextDispatcher () :> EntityDispatcher
                 ToggleDispatcher () :> EntityDispatcher
                 FpsDispatcher () :> EntityDispatcher
                 FeelerDispatcher () :> EntityDispatcher
                 FillBarDispatcher () :> EntityDispatcher
                 BlockDispatcher () :> EntityDispatcher
                 BoxDispatcher () :> EntityDispatcher
                 CharacterDispatcher () :> EntityDispatcher
                 TileMapDispatcher () :> EntityDispatcher]

        static member private makeDefaultFacets () =
            // TODO: consider if we shoud reflectively generate these
            Map.ofList
                [(typeof<NodeFacet>.Name, NodeFacet () :> Facet)
                 (typeof<EffectFacet>.Name, EffectFacet () :> Facet)
                 (typeof<ScriptFacet>.Name, ScriptFacet () :> Facet)
                 (typeof<TextFacet>.Name, TextFacet () :> Facet)
                 (typeof<RigidBodyFacet>.Name, RigidBodyFacet () :> Facet)
                 (typeof<TileMapFacet>.Name, TileMapFacet () :> Facet)
                 (typeof<StaticSpriteFacet>.Name, StaticSpriteFacet () :> Facet)
                 (typeof<AnimatedSpriteFacet>.Name, AnimatedSpriteFacet () :> Facet)]

        /// Make an empty world.
        static member makeEmpty (config : WorldConfig) =

            // ensure game engine is initialized
            Nu.init config.NuConfig

            // make the default plug-in
            let plugin = NuPlugin ()

            // make the world's event delegate
            let eventDelegate =
                let eventTracer = Log.remark "Event"
                let eventTracing = Core.getEventTracing ()
                let eventFilter = Core.getEventFilter ()
                let globalParticipant = Default.Game
                let globalParticipantGeneralized = { GpgAddress = atoa globalParticipant.GameAddress }
                EventSystemDelegate.make eventTracer eventTracing eventFilter globalParticipant globalParticipantGeneralized

            // make the game dispatcher
            let defaultGameDispatcher = World.makeDefaultGameDispatcher ()

            // make the world's dispatchers
            let dispatchers =
                { GameDispatchers = Map.ofList [defaultGameDispatcher]
                  ScreenDispatchers = World.makeDefaultScreenDispatchers ()
                  LayerDispatchers = World.makeDefaultLayerDispatchers ()
                  EntityDispatchers = World.makeDefaultEntityDispatchers ()
                  Facets = World.makeDefaultFacets ()
                  TryGetExtrinsic = World.tryGetExtrinsic
                  UpdateEntityInEntityTree = World.updateEntityInEntityTree
                  RebuildEntityTree = World.rebuildEntityTree }

            // make the world's subsystems
            let subsystems =
                Subsystems.make
                    (PhysicsEngineSubsystem.make (MockPhysicsEngine.make ()))
                    (RendererSubsystem.make (MockRenderer.make ()))
                    (AudioPlayerSubsystem.make (MockAudioPlayer.make ()))

            // make the world's scripting environment
            let scriptingEnv = Scripting.Env.Env.make ()

            // make the world's ambient state
            let ambientState =
                let overlayRoutes = World.dispatchersToOverlayRoutes dispatchers.EntityDispatchers
                let overlayRouter = OverlayRouter.make overlayRoutes
                let symbolStore = SymbolStore.makeEmpty ()
                AmbientState.make 1L (Metadata.makeEmpty ()) overlayRouter Overlayer.empty symbolStore None

            // make the world's spatial tree
            let spatialTree = World.makeEntityTree ()

            // make the world
            let world = World.make plugin eventDelegate dispatchers subsystems scriptingEnv ambientState spatialTree (snd defaultGameDispatcher)
            
            // subscribe to subscribe and unsubscribe events
            let world = World.subscribe World.handleSubscribeAndUnsubscribe Events.Subscribe Default.Game world
            let world = World.subscribe World.handleSubscribeAndUnsubscribe Events.Unsubscribe Default.Game world

            // finally, register the game
            World.registerGame world

        /// Make a default world with a default screen, layer, and entity, such as for testing.
        static member makeDefault () =
            let worldConfig = WorldConfig.defaultConfig
            let world = World.makeEmpty worldConfig
            let world = World.createScreen (Some Default.Screen.Name) world |> snd
            let world = World.createLayer (Some Default.Layer.Name) Default.Screen world |> snd
            let world = World.createEntity (Some Default.Entity.Name) DefaultOverlay Default.Layer world |> snd
            world

        /// Attempt to make the world, returning either a Right World on success, or a Left string
        /// (with an error message) on failure.
        static member tryMake (plugin : NuPlugin) sdlDeps config =

            // ensure game engine is initialized
            Nu.init config.NuConfig

            // attempt to create asset graph
            match AssetGraph.tryMakeFromFile Assets.AssetGraphFilePath with
            | Right assetGraph ->

                // make the world's event system
                let eventSystem =
                    let eventTracer = Log.remark "Event"
                    let eventTracing = Core.getEventTracing ()
                    let eventFilter = Core.getEventFilter ()
                    let globalParticipant = Default.Game
                    let globalParticipantGeneralized = { GpgAddress = atoa globalParticipant.GameAddress }
                    EventSystemDelegate.make eventTracer eventTracing eventFilter globalParticipant globalParticipantGeneralized

                // make plug-in dispatchers
                let pluginFacets = plugin.MakeFacets () |> List.map World.pairWithName
                let pluginGameDispatchers = plugin.MakeGameDispatchers () |> List.map World.pairWithName
                let pluginScreenDispatchers = plugin.MakeScreenDispatchers () |> List.map World.pairWithName
                let pluginLayerDispatchers = plugin.MakeLayerDispatchers () |> List.map World.pairWithName
                let pluginEntityDispatchers = plugin.MakeEntityDispatchers () |> List.map World.pairWithName

                // make the default game dispatcher
                let defaultGameDispatcher = World.makeDefaultGameDispatcher ()

                // make the world's dispatchers
                let dispatchers =
                    { GameDispatchers = Map.addMany pluginGameDispatchers (Map.ofList [defaultGameDispatcher])
                      ScreenDispatchers = Map.addMany pluginScreenDispatchers (World.makeDefaultScreenDispatchers ())
                      LayerDispatchers = Map.addMany pluginLayerDispatchers (World.makeDefaultLayerDispatchers ())
                      EntityDispatchers = Map.addMany pluginEntityDispatchers (World.makeDefaultEntityDispatchers ())
                      Facets = Map.addMany pluginFacets (World.makeDefaultFacets ())
                      TryGetExtrinsic = World.tryGetExtrinsic
                      UpdateEntityInEntityTree = World.updateEntityInEntityTree
                      RebuildEntityTree = World.rebuildEntityTree }

                // look up the active game dispather
                let activeGameDispatcherName = if config.StandAlone then plugin.GetStandAloneGameDispatcherName () else plugin.GetEditorGameDispatcherName ()
                let activeGameDispatcher = Map.find activeGameDispatcherName dispatchers.GameDispatchers

                // make the world's subsystems
                let subsystems =
                    let physicsEngine = FarseerPhysicsEngine.make Constants.Physics.Gravity
                    let physicsEngineSubsystem = PhysicsEngineSubsystem.make physicsEngine :> World Subsystem
                    let renderer =
                        match SdlDeps.getRenderContextOpt sdlDeps with
                        | Some renderContext -> SdlRenderer.make renderContext :> Renderer
                        | None -> MockRenderer.make () :> Renderer
                    renderer.EnqueueMessage (HintRenderPackageUseMessage Assets.DefaultPackage) // enqueue default package hint
                    let rendererSubsystem = RendererSubsystem.make renderer :> World Subsystem
                    let audioPlayer =
                        if SDL.SDL_WasInit SDL.SDL_INIT_AUDIO <> 0u
                        then SdlAudioPlayer.make () :> AudioPlayer
                        else MockAudioPlayer.make () :> AudioPlayer
                    audioPlayer.EnqueueMessage (HintAudioPackageUseMessage Assets.DefaultPackage) // enqueue default package hint
                    let audioPlayerSubsystem = AudioPlayerSubsystem.make audioPlayer :> World Subsystem
                    Subsystems.make physicsEngineSubsystem rendererSubsystem audioPlayerSubsystem

                // attempt to make the overlayer
                let intrinsicOverlays = World.makeIntrinsicOverlays dispatchers.Facets dispatchers.EntityDispatchers
                match Overlayer.tryMakeFromFile intrinsicOverlays Assets.OverlayerFilePath with
                | Right overlayer ->

                    // make the world's scripting environment
                    let scriptingEnv = Scripting.Env.Env.make ()

                    // make the world's ambient state
                    let ambientState =
                        let assetMetadataMap = Metadata.make assetGraph
                        let intrinsicOverlayRoutes = World.dispatchersToOverlayRoutes dispatchers.EntityDispatchers
                        let userOverlayRoutes = plugin.MakeOverlayRoutes ()
                        let overlayRoutes = intrinsicOverlayRoutes @ userOverlayRoutes
                        let overlayRouter = OverlayRouter.make overlayRoutes
                        let symbolStore = SymbolStore.makeEmpty ()
                        AmbientState.make config.TickRate assetMetadataMap overlayRouter overlayer symbolStore (Some sdlDeps)

                    // make the world's spatial tree
                    let spatialTree = World.makeEntityTree ()

                    // make the world
                    let world = World.make plugin eventSystem dispatchers subsystems scriptingEnv ambientState spatialTree activeGameDispatcher

                    // add the keyed values
                    let (kvps, world) = plugin.MakeKeyedValues world
                    let world = List.fold (fun world (key, value) -> World.addKeyedValue key value world) world kvps

                    // subscribe to subscribe and unsubscribe events
                    let world = World.subscribe World.handleSubscribeAndUnsubscribe Events.Subscribe Default.Game world
                    let world = World.subscribe World.handleSubscribeAndUnsubscribe Events.Unsubscribe Default.Game world

                    // try to load the prelude for the scripting language
                    match World.tryEvalPrelude world with
                    | Right struct (_, world) ->

                        // register the game
                        let world = World.registerGame world

#if DEBUG
                        // attempt to hookup the console if debugging
                        let world = WorldConsole.tryHookUp world |> snd
#endif

                        // fin
                        Right world
                    
                    // forward error messages
                    | Left struct (error, _) -> Left error
                | Left error -> Left error
            | Left error -> Left error