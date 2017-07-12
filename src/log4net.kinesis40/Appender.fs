namespace log4net.Appender

open System
open System.IO
open System.Linq
open System.Threading
open Amazon.Runtime
open Amazon.Runtime.CredentialManagement
open Amazon.Kinesis
open Amazon.Kinesis.Model

open log4net.Appender
open log4net.Core

type Agent<'T> = MailboxProcessor<'T>

[<AutoOpen>]
module internal Extensions =
    type IAmazonKinesis with
        member this.PutRecordAsync(req) =
            Async.FromBeginEnd(req, this.BeginPutRecord, this.EndPutRecord)

[<AutoOpen>]
module internal Model =
    type LogEvent =
        {
            LoggerName          : string
            Level               : string
            Timestamp           : DateTime
            ThreadName          : string
            CallerInfo          : string
            Message             : string
            ExceptionMessage    : string
            StackTrace          : string
        }

        member this.ToJson() = 
            sprintf "{\"LoggerName\":\"%s\",\"Level\":\"%s\",\"Timestamp\":\"%s\",\"ThreadName\":\"%s\",\"CallerInfo\":\"%s\",\"Message\":\"%s\",\"ExceptionMessage\":\"%s\",\"StackTrace\":\"%s\"}" 
                    this.LoggerName 
                    this.Level 
                    (this.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fffffff"))
                    this.ThreadName
                    this.CallerInfo
                    this.Message
                    this.ExceptionMessage
                    this.StackTrace

type KinesisAppender () as this =
    inherit AppenderSkeleton()

    [<DefaultValue>]
    val mutable _kinesis : AmazonKinesisClient

    let sharedFile = new SharedCredentialsFile()

    let genWorker _ = 
        let agent = Agent<LogEvent>.Start(fun inbox ->
            async {
                while true do
                    let! evt = inbox.Receive()
                    let payload = evt.ToJson() |> System.Text.Encoding.UTF8.GetBytes
                    use stream  = new MemoryStream(payload)
                    let req = new PutRecordRequest(StreamName   = this.StreamName,
                                                   PartitionKey = (if this.PartionKey = ""
                                                                  then Guid.NewGuid().ToString()
                                                                  else this.PartionKey),
                                                   Data         = stream)
                    do! this._kinesis.PutRecordAsync(req) |> Async.Ignore
            })
        agent.Error.Add(fun _ -> ()) // swallow exceptions so to stop agents from be coming useless after exception..

        agent
    
    let mutable workers : Agent<LogEvent>[] = [||]
    
    let initCount = ref 0
    let init () = 
        
        // make sure we only initialize the workers array once
        if Interlocked.CompareExchange(initCount, 1, 0) = 0 then
             let region = Amazon.RegionEndpoint.EnumerableAllRegions.FirstOrDefault(fun endpoint -> endpoint.SystemName = this.Region)
             this._kinesis <- match sharedFile.TryGetProfile this.Profile with
                               | true, profile -> let config = new AmazonKinesisConfig(RegionEndpoint = region)  // If you supply Profile you should supply Region as well (for now)
                                                  new AmazonKinesisClient(profile.GetAWSCredentials(sharedFile),config)
                               | _   -> new AmazonKinesisClient() // Fall back to config
             workers <- { 1..this.LevelOfConcurrency } |> Seq.map genWorker |> Seq.toArray

    let workerIdx = ref 0
    let send evt = 
        if workers.Length = 0 then init()

        let idx = Interlocked.Increment(workerIdx)
        if idx < workers.Length 
        then workers.[idx].Post evt
        else let idx' = idx % workers.Length
             Interlocked.CompareExchange(workerIdx, idx', idx) |> ignore
             workers.[idx'].Post evt

    member val StreamName = "" with get, set
    member val LevelOfConcurrency = 10 with get, set
    member val Region = "" with get, set
    member val Profile = "" with get, set
    member val PartionKey = "" with get, set

    override this.Append(loggingEvent : LoggingEvent) = 
        let exnMessage, stackTrace = 
            match loggingEvent.ExceptionObject with
            | null -> String.Empty, String.Empty
            | exn  -> exn.Message, exn.StackTrace

        let evt = { 
                    LoggerName          = loggingEvent.LoggerName
                    Level               = loggingEvent.Level.Name
                    Timestamp           = loggingEvent.TimeStamp
                    ThreadName          = loggingEvent.ThreadName
                    CallerInfo          = loggingEvent.LocationInformation.FullInfo
                    Message             = loggingEvent.RenderedMessage
                    ExceptionMessage    = exnMessage
                    StackTrace          = stackTrace
                  }
        send evt