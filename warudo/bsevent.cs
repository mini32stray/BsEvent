#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;
using Warudo.Core.Attributes;
using Warudo.Core.Graphs;

public class BsEventBroker : IDisposable
{
    public class BsEventMessage
    {
        public BsEventMessage(string _event, long time, JObject? status)
        {
            Event = _event;
            Time = time;
            Status = status;
        }
        public string Event{ get; }
        public long Time { get; }
        public JObject? Status { get; }
    }

    public ConcurrentQueue<BsEventMessage> MsgQueue { get; private set; }

    private readonly ClientWebSocket ws;
    private readonly Uri uri;
    public bool IsRunning { get; private set; }

    private byte[] recvBuffer;

    public BsEventBroker(string uri)
    {
        recvBuffer = new byte[16384];
        ws = new ClientWebSocket();
        this.uri = new Uri(uri);
        MsgQueue = new();
    }

    public async Task RunAsync(CancellationToken token)
    {
        IsRunning = true;
        Debug.Log("BsEvent:: WebSocket loop started.");
        Context.Service.Toast(
            Warudo.Core.Server.ToastSeverity.Info,
            "BsEvent",
            "WebSocket loop started");
        try
        {
            await RunAsyncImpl(token);
        }
        catch (OperationCanceledException)
        {
            Debug.Log("BsEvent:: cancelled.");
        }
        catch (WebSocketException ex)
        {
            Context.Service.Toast(
                Warudo.Core.Server.ToastSeverity.Error,
                "BsEvent::" + nameof(WebSocketException),
                ex.Message);
            Debug.LogError("BsEvent::" + nameof(WebSocketException) + " : " + ex.ToString());
            Debug.LogError("BsEvent:: WebSocket loop stopped with unexpected error.");
        }
        catch (Exception ex)
        {
            Context.Service.Toast(
                Warudo.Core.Server.ToastSeverity.Error,
                $"Unexpected error. see log for more detail. (${ex.Message})",
                ex.Message);
            Debug.LogError("BsEvent:: WebSocket loop stopped with unexpected error.");
            Debug.LogError(ex.ToString());
        }
        finally
        {
            Context.Service.Toast(
                Warudo.Core.Server.ToastSeverity.Info,
                "BsEvent",
                "WebSocket loop stopped");
            Debug.Log("BsEvent:: WebSocket loop stopped.");
            IsRunning = false;
        }
    }

    private async Task RunAsyncImpl(CancellationToken token)
    {
        if (ws.State == WebSocketState.Open)
        {
            return;
        }
        await ws.ConnectAsync(uri, token).ConfigureAwait(false);
        while (ws.State == WebSocketState.Open)
        {
            var result = await ReceiveAllTextAsync(token);
            if (result.isClosed)
            {
                Debug.Log("BsEvent:: websocket closed.");
                return;
            }
            if (result.text != null)
            {
                ParseReceived(result.text);
            }
        }
    }

    private async Task<(bool isClosed, string? text)> ReceiveAllTextAsync(CancellationToken token)
    {
        using var ms = new MemoryStream(capacity: 16384);
        for (var i = 0; i < 1000; i++)
        {
            var result = await ws.ReceiveAsync(recvBuffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, token);
                return (true, string.Empty);
            }
            if (result.Count > 0)
            {
                ms.Write(recvBuffer, 0, result.Count);
            }
            if (result.EndOfMessage)
            {
                string text =
                    ms.TryGetBuffer(out ArraySegment<byte> seg) ?
                    Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count) :
                    Encoding.UTF8.GetString(ms.ToArray());
                return (false, text);
            }
        }
        Debug.Log("BsEvent:: failed to receive websocket message.");
        return (false, null);
    }

    private JObject? ParseSilently(string text)
    {
        try
        {
            var j = JObject.Parse(text);
            return j;
        }
        catch (Exception ex)
        {
            Debug.LogError($"BsEvent:: Error while parsing JSON: {text}");
            Debug.LogError(ex.ToString());
            return null;
        }
    }

    private void ParseReceived(string text)
    {
        var j = ParseSilently(text);
        if (j == null)
        {
            return;
        }
        var e = j.Value<string>("event");
        var t = j.Value<long>("time");
        if (e == null)
        {
            Debug.LogError($"error while parsing received json: {text}");
            return;
        }
        var msg = new BsEventMessage(e, t, null);
        MsgQueue.Enqueue(msg);
    }

    public void Dispose()
    {
        ws.Dispose();
    }
}

[NodeType(Id = "004bec34-1e1a-4171-aa6b-c8ca25bb0145", Title = "BsEvent")]
public class BsEventNode : Node
{
    class AutoStarter
    {
        private DateTime lastAlive;
        private DateTime lastInvoked;

        public AutoStarter(DateTime now)
        {
            lastAlive = now;
            lastInvoked = now;
        }

        public void OnUpdate(
            DateTime now,
            Func<bool> checkIsAlive,
            Action start)
        {
            if (checkIsAlive())
            {
                lastAlive = now;
            }
            else
            {
                if (now < lastInvoked + TimeSpan.FromSeconds(15))
                {
                    return;
                }
                if (lastAlive + TimeSpan.FromSeconds(15) < now)
                {
                    start();
                    lastInvoked = now;
                }
            }
        }
    }

    BsEventBroker? broker;
    CancellationTokenSource? cts;

    [FlowOutput]
    public Continuation? Hello = null;
    [FlowOutput]
    public Continuation? SongStart = null;
    [FlowOutput]
    public Continuation? NoteCut = null;
    [FlowOutput]
    public Continuation? ObstacleEnter = null;
    [FlowOutput]
    public Continuation? ObstacleExit = null;
    [FlowOutput]
    public Continuation? NoteMissed = null;
    [FlowOutput]
    public Continuation? BombCut = null;
    [FlowOutput]
    public Continuation? BombMissed = null;
    [FlowOutput]
    public Continuation? Finished = null;
    [FlowOutput]
    public Continuation? Failed = null;
    [FlowOutput]
    public Continuation? SoftFailed = null;
    [FlowOutput]
    public Continuation? ScoreChanged = null;
    [FlowOutput]
    public Continuation? EnergyChanged = null;
    [FlowOutput]
    public Continuation? NoteSpawned = null;
    [FlowOutput]
    public Continuation? Other = null;
    [FlowOutput]
    public Continuation? BeatmapEvent = null;
    [FlowOutput]
    public Continuation? Pause = null;
    [FlowOutput]
    public Continuation? Resume = null;
    [FlowOutput]
    public Continuation? Menu = null;

    [DataInput]
    public bool autoStart = false;

    [Trigger]
    [DisabledIf(nameof(IsActive))]
    public void Start()
    {
        if (broker != null || cts != null)
        {
            Debug.LogWarning("BsEvent:: previous broker or cts was not cleared successfully. Invoking force reset.");
            ForceReset();
        }
        var uri = string.IsNullOrWhiteSpace(ws_address) ? "ws://127.0.0.1:6557/socket" : ws_address;
        broker = new(uri!);
        cts = new CancellationTokenSource();
        _ = broker
            .RunAsync(cts.Token)
            .ContinueWith(task =>
            {
                ForceReset();
            });
    }

    [Trigger]
    [DisabledIf(nameof(IsNotActive))]
    public void Stop()
    {
        cts?.Cancel();
    }

    [DataInput]
    public string? ws_address;

    private Dictionary<string, string> exits;
    private AutoStarter autoStarter;

    public BsEventNode()
    {
        exits = new()
        {
            { "hello", nameof(Hello) },
            { "songStart", nameof(SongStart) },
            { "noteCut", nameof(NoteCut) },
            { "obstacleEnter", nameof(ObstacleEnter) },
            { "obstacleExit", nameof(ObstacleExit) },
            { "noteMissed", nameof(NoteMissed) },
            { "bombCut", nameof(BombCut) },
            { "bombMissed", nameof(BombMissed) },
            { "finished", nameof(Finished) },
            { "failed", nameof(Failed) },
            { "softFailed", nameof(SoftFailed) },
            { "scoreChanged", nameof(ScoreChanged) },
            { "energyChanged", nameof(EnergyChanged) },
            { "noteSpawned", nameof(NoteSpawned) },
            { "other", nameof(Other) },
            { "beatmapEvent", nameof(BeatmapEvent) },
            { "pause", nameof(Pause) },
            { "resume", nameof(Resume) },
            { "menu", nameof(Menu) },
        };
        autoStarter = new(DateTime.Now);
    }

    public void ForceReset()
    {
        cts?.Dispose();
        cts = null;
        broker?.Dispose();
        broker = null;
    }

    protected override void OnDestroy()
    {
        cts?.Cancel();
        broker?.Dispose();
        base.OnDestroy();
    }

    public override void OnUpdate()
    {
        base.OnUpdate();
        if (autoStart)
        {
            autoStarter.OnUpdate(
                DateTime.Now,
                IsActive,
                Start);
        }

        if (broker == null)
            {
                return;
            }
        if (!broker.MsgQueue.IsEmpty)
        {
            if (broker.MsgQueue.TryDequeue(out var x))
            {
                //Debug.Log($"BsEvent:: {x.Event}");
                if (exits.TryGetValue(x.Event, out var exit))
                {
                    InvokeFlow(exit);
                }
            }
            else
            {
                Debug.LogWarning("BsEvent:: Queue is not empty but faild to get. unexpected situation.");
            }
        }
    }

    private bool IsActive() => broker?.IsRunning ?? false;
    private bool IsNotActive() => !IsActive();
}
