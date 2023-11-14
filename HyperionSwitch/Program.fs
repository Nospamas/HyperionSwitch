open System
open System.Diagnostics
open System.Net.Http
open Newtonsoft.Json

let args = Environment.GetCommandLineArgs()

type RequestType =
    | IsRunning
    | TurnOn
    | TurnOff

type MessageTarget =
    | Hyperion
    | ScreenCapture

let client = new HttpClient()


type ComponentsResponse = { Info: Infos }
and Infos = { Components: ComponentState array }
and ComponentState = { Name: string; Enabled: bool }

let createHyperionToggleRequest (state: bool) =
    {| command = "componentstate"
       componentstate = {| component = "ALL"; state = state |} |}

let createHyperionServerInfoRequest () = {| command = "serverinfo" |}

let createHyperionRequest (requestType: RequestType) : string =
    let request: Object =
        match requestType with
        | TurnOn -> createHyperionToggleRequest true
        | TurnOff -> createHyperionToggleRequest false
        | IsRunning -> createHyperionServerInfoRequest ()

    request |> JsonConvert.SerializeObject

let deserializeServerInfoResponse (response: HttpResponseMessage) =
    task {
        let! bodyText = response.Content.ReadAsStringAsync()
        return JsonConvert.DeserializeObject<ComponentsResponse>(bodyText)
    }
    |> Async.AwaitTask

let hyperionUrl = "http://hyperion.box.cansk.net:8090/json-rpc"
let screenCaptureUrl = sprintf "http://localhost:9191/API/?command=%s"

let send (dest: MessageTarget) (requestType: RequestType) =
    let url =
        match dest with
        | Hyperion -> hyperionUrl
        | ScreenCapture ->
            match requestType with
            | IsRunning -> screenCaptureUrl "STATE"
            | TurnOn -> screenCaptureUrl "ON"
            | TurnOff -> screenCaptureUrl "OFF"

    let message =
        match dest with
        | Hyperion -> createHyperionRequest requestType
        | ScreenCapture -> ""

    task { return! client.PostAsync(url, new StringContent(message)) }
    |> Async.AwaitTask

let isScreenCaptureRunning () =
    let processes = Process.GetProcessesByName "HyperionScreenCap"
    let processesCount = (Seq.length processes)
    processesCount > 0

let startHyperionIfNotRunning () =
    if not (isScreenCaptureRunning ()) then
        Process.Start(@"C:\Program Files (x86)\Hyperion Screen Capture\HyperionScreenCap.exe")
        |> ignore
    else
        ()

let isScreenCaptureEnabled () =
    task {
        let! result = send ScreenCapture IsRunning
        let! content = result.Content.ReadAsStringAsync()
        return content = "True"
    }
    |> Async.AwaitTask

let isOnRequest = Seq.contains "--on" args
let isOffRequest = Seq.contains "--off" args

let testHyperionIsRunning () : Async<bool> =
    async {
        let! apiResponse = send Hyperion IsRunning
        let! response = deserializeServerInfoResponse apiResponse
        return Seq.contains { Name = "ALL"; Enabled = true } response.Info.Components
    }

// main control flow
if isOnRequest then
    async {
        let! hyperionRunning = testHyperionIsRunning ()

        if not hyperionRunning then
            printfn "Hyperion not turned on, turning on"
            send Hyperion TurnOn |> ignore
        else
            printfn "Hyperion turned on, "
            let screenCaptureRunning = isScreenCaptureRunning ()

            if not screenCaptureRunning then
                printfn "Screen capture not turned on, turning on"
                // this will auto reconnect to the server, no need to send http message to reconnect
                startHyperionIfNotRunning ()
            
            
            let! screenCaptureEnabled = isScreenCaptureEnabled ()
            if not screenCaptureEnabled then
                printfn "Screen capture not enabled, turning on"
                send ScreenCapture TurnOn |> ignore
            else
                printfn "Screen capture already on, doing nothing"
                ()
    }
    |> Async.RunSynchronously

if isOffRequest then
    async {
        let screenCaptureRunning = isScreenCaptureRunning ()
        
        if screenCaptureRunning then
            let! screenCaptureEnabled = isScreenCaptureEnabled ()
            if screenCaptureEnabled then
                printfn "Screen capture enabled, turning off"
                send ScreenCapture TurnOff |> ignore
            else
                printfn "Screen capture disabled, turning off hyperion"
                let! hyperionRunning = testHyperionIsRunning ()
                if hyperionRunning then
                    send Hyperion TurnOff |> ignore
        else
            let! hyperionRunning = testHyperionIsRunning ()
            if hyperionRunning then
                printfn "Screen capture not running, turning off hyperion"
                send Hyperion TurnOff |> ignore   
    }
    |> Async.RunSynchronously