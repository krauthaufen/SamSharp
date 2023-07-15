namespace SamSharp.Demo

open SamSharp
open Adaptify
open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module Shader =
    open FShade
    let color =
        sampler2d {
            texture uniform?DiffuseColorTexture
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
            filter Filter.MinMagMipLinear
        }
    let mask =
        sampler2d {
            texture uniform?Mask
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
            filter Filter.MinMagLinear
        }
    
    let texture (v : Effects.Vertex) =
        fragment {
            let c = color.Sample(v.tc)
            let m = mask.Sample(v.tc).X * 0.6
            
            return V4d(lerp c.XYZ V3d.IOO m, 1.0)
        }

type Message =
    | CameraMessage of CameraController.Message
    | SetFile of string
    | SetIndex of PixImage * SamIndex
    | SetMask of Matrix<float32>
    | Clear

module App =
    
    let sam = new Sam()
    
    let initial =
        {
            Camera = CameraController.initial
            File = None
            Image = None
            Index = None
            Mask = None
        }
    
    let update (_env : Env<Message>) (model : Model) (msg : Message) =
        match msg with
        | CameraMessage message -> { model with Camera = CameraController.update model.Camera message }
        | SetFile s ->
            { model with File = Some s; Index = None; Mask = None }
        | SetIndex (img, samIndex) ->
            let box = Box2d.FromCenterAndSize(V2d.Zero, V2d img.Size)
            let cam = CameraController.update model.Camera (CameraController.SetSceneBounds box)
            let cam = CameraController.update cam (CameraController.BestFit box)
            { model with
                Index = Some samIndex
                Image = Some img
                Mask = None
                Camera = cam
            }
        | SetMask m -> { model with Mask = Some m }
        | Clear -> { model with File = None; Index = None; Mask = None }
        
    let view (file : option<string>) (env : Env<Message>) (m : AdaptiveModel) =
        
        let texture = 
            m.Image |> AVal.map (function
                | Some img -> PixTexture2d(PixImageMipMap [| img |], TextureParams.mipmapped) :> ITexture
                | None -> NullTexture() :> ITexture
            )
            
        let maskTexture =
            m.Mask |> AVal.map (function
                | Some m ->
                    let img = PixImage<float32>(Col.Format.Gray, m.AsVolume())
                    PixTexture2d(PixImageMipMap [| img :> PixImage |], TextureParams.empty) :> ITexture
                | None ->
                    NullTexture() :> ITexture
            )
        
        body {
            Style [
                Padding "0"
                Margin "0"
                Position "fixed"
                Width "100%"
                Height "100%"
            ]
            
            let startLoad (name : string) (data : byte[]) =
                env.Emit [SetFile name] 
                
                task {
                    do! Async.SwitchToThreadPool()
                    try
                        use ms = new System.IO.MemoryStream(data)
                        let img = PixImageSharp.Create ms
                        let idx = sam.BuildIndex img
                        env.Emit [SetIndex(img, idx)]
                    with _ ->
                        env.Emit [Clear]
                } |> ignore
            
            
            dropOverlay {
                Style [
                    Color "white"
                    FontSize "80px"
                    FontFamily "monospace"
                ]
                
                DropOverlay.OnDrop (fun things ->
                    let data =
                        things |> List.tryPick (function
                            | DropValue.File(name, mime, data) when mime.StartsWith "image/" ->
                                Some (name, data)
                            | DropValue.Text("text/plain", str) ->
                                if System.IO.File.Exists str then
                                    try Some (System.IO.Path.GetFileName str, File.readAllBytes str)
                                    with _ -> None
                                else
                                    None
                            | _ ->
                                None   
                        )
                    match data with
                    | Some (name, data) -> startLoad name data
                    | None -> ()
                )
                
                "+"
            }
            
            div {
                Style [
                    UserSelect "none"
                    Background "rgb(30 30 30)"
                    Width "100%"
                    Height "100%"
                    Position "fixed"
                    Top "0px"
                    Left "0px"
                    ZIndex 20
                    Color "white"
                    FontSize "40px"
                    FontFamily "monospace"
                    JustifyContent "center"
                    AlignItems "center"
                ]
                m.File |> AVal.bind (function
                    | Some _ ->
                        m.Index |> AVal.map (function
                            | Some _ -> Style [Display "none"]
                            | None -> Style [Display "flex"]
                        )
                    | None ->
                        Style [Display "flex"]
                        |> AVal.constant
                )
                
                
                let content =
                    m.File |> AVal.map (function
                        | None -> "Drop an Image here to start"
                        | Some f -> sprintf "Preparing '%s' ..." (System.IO.Path.GetFileName f)
                    )
                
                content
                
                
            }
            
            renderControl {
                
                Style [
                    Background "rgb(30 30 30)"
                    Width "100%"
                    Height "100%"
                ]
                CameraController.attributes (Env.map CameraMessage env) (fun e -> true) (fun e -> true) (AVal.constant m.Camera)
                
                Sg.View (m.Camera.camera |> AVal.map (fun c -> CameraView.viewTrafo c.cameraView))
                Sg.Proj (m.Camera.camera |> AVal.map (fun c -> Frustum.projTrafo c.frustum))
                
                Sg.OnTap(fun e ->
                    match AVal.force m.Index, AVal.force m.Image with
                    | Some idx, Some img ->
                        let box = Box2d.FromCenterAndSize(V2d.Zero, V2d img.Size)
                        let px = V2i(round (e.WorldPosition.XY - box.Min))
                        let px = V2i(px.X, img.Size.Y - px.Y - 1)
                        let m = idx.Query [ Query.Point(px, 1) ]
                        env.Emit [SetMask m]
                    | _ ->
                        ()
                )
                RenderControl.OnReady (fun _ ->
                    match file with
                    | Some file when System.IO.File.Exists file ->
                        startLoad (System.IO.Path.GetFileName file) (System.IO.File.ReadAllBytes file)
                    | _ ->
                        ()
                )
                RenderControl.OnResize (fun e ->
                    env.Emit [ CameraMessage (CameraController.Resize e.Size) ]
                )
                
                RenderControl.OnRendered (fun e ->
                    if AVal.force m.Camera.needsAnimation then
                        env.Emit [ CameraMessage(CameraController.Interpolate) ]    
                )
                
                sg {
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        Shader.texture
                    }
                    Sg.Uniform("DiffuseColorTexture", texture)
                    Sg.Uniform("Mask", maskTexture)
                    
                    let trafo =
                        m.Image |> AVal.map (function
                            | Some img -> Trafo3d.Scale(float img.Size.X / 2.0, float img.Size.Y / 2.0, 1.0)
                            | None -> Trafo3d.Identity
                        )
                    Sg.Trafo(trafo)
                    Primitives.ScreenQuad 0.0
                }
            }
            
            div {
                Style [
                    Position "fixed"; Top "0px"; Left "0px"
                    PaddingRight "1em"
                    Background "rgba(0,0,0,0.5)"; BackdropFilter "blur(10px)"
                    FontFamily "monospace"
                    FontSize "1em"
                    BorderBottomRightRadius "1em"
                    Color "white"
                ]
                
                ul {
                    li { "pan with the left mouse button" }
                    li { "zoom using the scroll-wheel" }
                    li { "drop an image in the window to load it" }
                    li { "left-click to start segmentation" }
                }
            }
            
        }

    let app file =
        {
            initial = initial
            update = update
            view = view file
            unpersist = Unpersist.instance 
        }