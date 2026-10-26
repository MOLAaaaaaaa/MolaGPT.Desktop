using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MolaGPT.Core.Chat.Agents.Pi;
using MolaGPT.Core.Remote;

namespace MolaGPT.Desktop.Services.Remote;

/// <summary>
/// The anonymous (account-less) half of remote control. This machine signs in
/// as a device key, pairs phones with a room code plus a manually compared
/// six-digit code, and then only ever exchanges end-to-end AES-GCM frames with
/// them — the relay relays ciphertext and nothing else. It runs alongside the
/// account link; its peers carry <see cref="AnonPeerPrefix"/>.
/// </summary>
public sealed partial class RemoteControlHost
{
    private const int MaxPhoneNameChars = 60;
    private const int MaxHandshakesPerRoom = 3;
    private const int MaxHelloStartsPerMinute = 6;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PendingSessionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _anonTicketWake = new(0);
    private readonly Dictionary<string, AnonPeerSession> _anonymousSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<long>> _anonHelloStarts = new(StringComparer.Ordinal);
    private AnonPairingRoom? _anonRoom;

    /// <summary>A pairing is waiting for the user to compare the two codes.</summary>
    public sealed record AnonymousConfirmRequest(string PhoneName, string Sas, DateTimeOffset Deadline);

    public string? AnonymousRoomCode { get { lock (_gate) return _anonRoom is { Expired: false } room ? room.Code : null; } }
    public DateTimeOffset? AnonymousRoomExpiresAt { get { lock (_gate) return _anonRoom is { Expired: false } room ? room.ExpiresAt : null; } }
    public AnonymousConfirmRequest? AnonymousConfirm { get { lock (_gate) return _anonRoom?.Confirm; } }

    public event Action? AnonymousPairingChanged;

    /// <summary>Starts the anonymous link: registers the device key on first
    /// use, then connects the relay as the device of the #dev: namespace.</summary>
    public async Task StartAnonymousAsync(CancellationToken ct = default)
    {
        if (_anonApi is null || AnonymousRunning) return;
        try
        {
            await EnsureRegisteredAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"anon register: {ex.Message}");
        }
        StartLink(_anonLink);
    }

    private async Task EnsureRegisteredAsync(CancellationToken ct)
    {
        var api = _anonApi!;
        JsonObject[] grants;
        try
        {
            grants = await api.GrantListAsync(ct).ConfigureAwait(false);
        }
        catch (AnonApiException ex) when (ex.Code is "unknown_device" or "revoked")
        {
            await api.RegisterDeviceAsync(ct).ConfigureAwait(false);
            Log("anon device registered");
            return;
        }

        // Reconcile both ways. A server grant this desk no longer recognises
        // (revoked while offline, or a pairing that crashed before activating) is
        // revoked now; a local pairing the server no longer has (the phone removed
        // this computer) is dropped.
        foreach (var pair in _phones.AnonymousList().Where(p => p.State == PairedPhoneStore.PendingActivate))
            _phones.SetAnonymousState(pair.PairId, PairedPhoneStore.PendingRevoke);
        var onServer = grants.Select(g => g["pairId"]?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var local = _phones.AnonymousList().Select(p => p.PairId).ToHashSet(StringComparer.Ordinal);
        foreach (var pairId in local.Where(id => !onServer.Contains(id))) _phones.Remove(pairId);
        var pending = _phones.AnonymousPendingRevokes();
        foreach (var pairId in onServer.Where(id => !local.Contains(id) && !pending.Contains(id)))
            _phones.AddAnonymous(pairId, "", [], PairedPhoneStore.PendingRevoke);
        await FlushAnonRevokesAsync().ConfigureAwait(false);
    }

    /// <summary>Opens a pairing room and shows its code. The local window is
    /// authoritative: nothing a server sends can reopen it.</summary>
    public async Task BeginAnonymousPairingAsync(CancellationToken ct = default)
    {
        if (_anonApi is null) return;
        // First pairing has no grant yet; the host still needs to be on the
        // relay, so start it now if it is not running.
        if (!AnonymousRunning) await StartAnonymousAsync(ct).ConfigureAwait(false);
        var reply = await _anonApi.OpenRoomAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _anonRoom = new AnonPairingRoom
            {
                Code = reply["room"]!.GetValue<string>(),
                RoomId = AnonText.FromBase64Url(reply["roomId"]!.GetValue<string>()),
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(reply["expiresAt"]!.GetValue<long>()),
            };
        }
        _anonTicketWake.Release();
        AnonymousPairingChanged?.Invoke();
    }

    /// <summary>Closes the room and cancels a pending confirm dialog.</summary>
    public void CancelAnonymousPairing()
    {
        lock (_gate) _anonRoom = null;
        AnonymousPairingChanged?.Invoke();
        _ = CloseServerRoomAsync();
    }

    private async Task CloseServerRoomAsync()
    {
        try { if (_anonApi is not null) await _anonApi.CloseRoomAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* the local window is closed either way */ }
    }

    /// <summary>The user compared the codes and answered. Bound to the current
    /// handshake: a request for an older one is refused, not reused.</summary>
    public async Task ConfirmAnonymousPairingAsync(bool allow)
    {
        AnonPairingHandshake? handshake = null;
        AnonPairingRoom? room = null;
        string peer = string.Empty;
        lock (_gate)
        {
            room = _anonRoom;
            if (room is null) return;
            if (room.Confirm is not null && DateTimeOffset.UtcNow > room.Confirm.Deadline) allow = false;
            handshake = room.Active;
            peer = handshake?.Peer ?? string.Empty;
            room.Active = null;
            room.Confirm = null;
            // On allow the room stays until the grant call returns: closing the
            // window meanwhile is what cancels the pairing (checked below).
            if (!allow || handshake is null) _anonRoom = null;
        }
        AnonymousPairingChanged?.Invoke();
        if (!allow || handshake is null)
        {
            if (peer.Length > 0) SendControlToPeer(peer, ErrorFrame("declined"));
            _ = CloseServerRoomAsync();
            return;
        }

        var pairId = RandomNumberGenerator.GetBytes(16);
        var pairIdText = AnonText.ToBase64Url(pairId);
        // Persisted before the server knows about it: a crash in between leaves
        // an unusable pending pairing, never an unrecorded working one.
        _phones.AddAnonymous(pairIdText, handshake.PhoneName, handshake.Keys!.Master, PairedPhoneStore.PendingActivate);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _anonApi!.GrantAddAsync(pairIdText, AnonText.ToBase64Url(handshake.GrantPub), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"anon grant add: {ex.Message}");
            _phones.Remove(pairIdText);
            lock (_gate) if (ReferenceEquals(_anonRoom, room)) _anonRoom = null;
            AnonymousPairingChanged?.Invoke();
            SendControlToPeer(handshake.Peer, ErrorFrame("server_unavailable"));
            _ = CloseServerRoomAsync();
            return;
        }

        bool cancelled;
        lock (_gate)
        {
            cancelled = !ReferenceEquals(_anonRoom, room);
            if (!cancelled) _anonRoom = null;
        }
        AnonymousPairingChanged?.Invoke();
        if (cancelled)
        {
            // The window closed while the grant call was in flight; the grant is
            // queued for removal and the phone gets nothing.
            _phones.SetAnonymousState(pairIdText, PairedPhoneStore.PendingRevoke);
            _ = FlushAnonRevokesAsync();
            return;
        }
        _phones.SetAnonymousState(pairIdText, PairedPhoneStore.Active);
        var finish = AnonPairingV2.SealFinish(handshake.Keys.Finish, handshake.Transcript!, new JsonObject
        {
            ["pairId"] = pairIdText,
            ["machineName"] = _machineName,
            ["confirm"] = AnonPairingV2.ConfirmValue(handshake.Keys.Confirm, handshake.Transcript!, pairId),
            ["directoryKey"] = AnonText.ToBase64Url(_anonApi!.Identity.DirectoryKeyGetOrCreate()),
        });
        SendControlToPeer(handshake.Peer, new JsonObject
        {
            ["v"] = 2, ["ctl"] = "pair2_finish", ["ct"] = finish.Ct, ["tag"] = finish.Tag,
        });
        Log($"anon paired {handshake.PhoneName}");
        Paired?.Invoke(handshake.PhoneName, false);
        _ = CloseServerRoomAsync();
    }

    private static JsonObject ErrorFrame(string reason) => new() { ["v"] = 2, ["ctl"] = "pair2_error", ["reason"] = reason };

    /// <summary>Three-layer revoke: local master unusable now, server grant removed,
    /// and the live connection dropped. Offline, the server part waits in the store
    /// as PendingRevoke and is retried on the next start or revoke.</summary>
    public void UnpairAnonymous(string pairIdText)
    {
        string? peer = null;
        lock (_gate)
        {
            if (_anonymousSessions.Values.FirstOrDefault(s => s.PairId == pairIdText) is { } session)
            {
                peer = session.Peer;
                _anonymousSessions.Remove(peer);
            }
            _subscriptions.Remove(peer ?? string.Empty);
        }
        if (peer is not null) ShedPeer(peer);
        DropUploads(upload => upload.PairId == pairIdText);
        _phones.SetAnonymousState(pairIdText, PairedPhoneStore.PendingRevoke);
        // Session tool grants may have come from this phone; dropping them all is
        // the safe direction (the desk re-approves what it still wants).
        _grantStore?.ClearSessionGrants();
        _ = FlushAnonRevokesAsync();
    }

    private int _flushingRevokes;

    private async Task FlushAnonRevokesAsync()
    {
        if (_anonApi is null || Interlocked.Exchange(ref _flushingRevokes, 1) == 1) return;
        try
        {
            foreach (var pairId in _phones.AnonymousPendingRevokes())
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _anonApi.GrantRemoveAsync(pairId, timeout.Token).ConfigureAwait(false);
                    _phones.Remove(pairId);
                }
                // The server answered and refused: removal is idempotent there, so a
                // refusal means it holds no such grant. Nothing left to retry.
                catch (AnonApiException) { _phones.Remove(pairId); }
                catch (Exception) { /* network: stays PendingRevoke, retried on the next start or revoke */ }
            }
        }
        finally { Volatile.Write(ref _flushingRevokes, 0); }
    }

    private async Task OnAnonymousStoppedAsync()
    {
        lock (_gate) _anonRoom = null;
        AnonymousPairingChanged?.Invoke();
        await FlushAnonRevokesAsync().ConfigureAwait(false);
    }

    // ---- inbound frames ---------------------------------------------------------

    private void AnonOnPeerFrame(string peer, JsonObject body)
    {
        switch (Text(body, "ctl"))
        {
            case "hello":
                AnonHandleHello(peer, body);
                return;
            case "pair2_commit":
                AnonHandleCommit(peer, body);
                return;
            case "pair2_reveal":
                AnonHandleReveal(peer, body);
                return;
        }
        if (body["ct"] is null || body["tag"] is null) return;
        AnonPeerSession? session;
        lock (_gate) _anonymousSessions.TryGetValue(peer, out session);
        if (session is null) return;
        JsonObject plain;
        try { plain = session.Phone.Open(body); }
        catch (CryptographicException)
        {
            Log("anon frame failed authentication; dropping peer");
            AnonDropSession(peer);
            return;
        }
        if (!session.Ready)
        {
            if (Text(plain, "ctl") == "ready")
            {
                session.Ready = true;
                _phones.Touch(session.PairId);
                // The same phone paired both ways shows up twice; the code pairing wins on
                // both ends: this side drops the account record, the phone drops its key.
                if (Text(plain, "deviceId") is { Length: > 0 } phoneDevice) _phones.RemoveAccountDevice(phoneDevice);
                JsonObject readyAck;
                lock (session.SendLock) readyAck = session.Desktop.Seal(new JsonObject
                {
                    ["v"] = 2, ["ctl"] = "ready_ack", ["accountMachineId"] = _machineId,
                });
                SendFrame(peer, readyAck);
            }
            else AnonDropSession(peer);
            return;
        }
        if (Text(plain, "ctl") is not null) return;
        _ = HandleRequestAsync(peer, plain, session.PairId);
    }

    private void AnonHandleHello(string peer, JsonObject body)
    {
        var pairText = Text(body, "pair");
        var proof = Text(body, "proof");
        var nP = B64uField(body, "nP", 32);
        if (pairText is null || proof is null || nP is null) return;
        lock (_gate)
        {
            // A repeated hello never resets a working session.
            if (_anonymousSessions.ContainsKey(peer)) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var starts = _anonHelloStarts.TryGetValue(pairText, out var queue) ? queue : _anonHelloStarts[pairText] = [];
            while (starts.Count > 0 && now - starts.Peek() > 60_000) starts.Dequeue();
            if (starts.Count >= MaxHelloStartsPerMinute) return;
            starts.Enqueue(now);
        }
        var pairId = AnonText.FromBase64Url(pairText);
        if (pairId.Length != 16) return;
        var master = _phones.AnonymousSecret(pairText);
        if (master is null) return;
        var deviceId = _anonApi!.Identity.DeviceId;
        var connectKey = AnonSessionV2.ConnectKey(master, pairId);
        var expected = AnonSessionV2.ProofPhone(connectKey, deviceId, pairId, nP);
        if (!AnonText.FixedTimeEqual(AnonText.FromBase64Url(proof), AnonText.FromBase64Url(expected))) return;

        var nD = RandomNumberGenerator.GetBytes(32);
        var keys = AnonSessionV2.DeriveSession(master, deviceId, pairId, nP, nD);
        var session = new AnonPeerSession
        {
            Peer = peer,
            PairId = pairText,
            PairIdBytes = pairId,
            Master = master,
            Keys = keys,
            Phone = new AnonSessionV2.Direction(keys.PhoneToDesktop, deviceId, pairId, keys.S, "p2d"),
            Desktop = new AnonSessionV2.Direction(keys.DesktopToPhone, deviceId, pairId, keys.S, "d2p"),
            EstablishedAt = DateTimeOffset.UtcNow,
        };
        // The phone reconnected (the relay already replaced its old socket, and the
        // proof above shows it holds the master): its old session goes.
        lock (_gate)
        {
            foreach (var old in _anonymousSessions.Where(s => s.Value.PairId == pairText).Select(s => s.Key).ToList())
            {
                _anonymousSessions.Remove(old);
                _subscriptions.Remove(old);
            }
            _anonymousSessions[peer] = session;
        }
        SendControlToPeer(peer, new JsonObject
        {
            ["v"] = 2, ["ctl"] = "offer", ["pair"] = pairText,
            ["nP"] = AnonText.ToBase64Url(nP), ["nD"] = AnonText.ToBase64Url(nD),
            ["proof"] = AnonSessionV2.ProofDesktop(connectKey, deviceId, pairId, nP, nD),
        });
    }

    private void AnonHandleCommit(string peer, JsonObject body)
    {
        var roomId = B64uField(body, "roomId", 16);
        var handshakeId = B64uField(body, "handshakeId", 16);
        var commit = B64uField(body, "C", 32);
        var grantPub = B64uField(body, "grantPub", 91);
        var phoneName = Text(body, "phoneName");
        if (roomId is null || handshakeId is null || commit is null || grantPub is null
            || string.IsNullOrWhiteSpace(phoneName) || phoneName.Length > MaxPhoneNameChars
            || !AnonCrypto.IsP256Spki(grantPub)) return;
        // Used verbatim: the phone's commitment covers the exact string it sent.

        AnonPairingRoom room;
        lock (_gate)
        {
            if (_anonRoom is not { Expired: false } current) return;
            room = current;
            // One handshake at a time, a busy desk never swaps in a newer asker,
            // and each room hands out at most three chances.
            if (room.Active is not null || room.HandshakesStarted >= MaxHandshakesPerRoom) return;
            if (!room.RoomId.AsSpan().SequenceEqual(roomId)) return;
        }

        var ephemeral = AnonCrypto.NewEphemeralKey();
        var nD = RandomNumberGenerator.GetBytes(32);
        var ephPubD = ephemeral.ExportSubjectPublicKeyInfo();
        var identity = _anonApi!.Identity;
        var signature = identity.Sign(AnonPairingV2.OfferSignatureInput(
            identity.DeviceId, roomId, room.Code, handshakeId, commit, grantPub, phoneName,
            ephPubD, nD, identity.PublicKey));
        var handshake = new AnonPairingHandshake
        {
            Peer = peer,
            RoomId = roomId,
            HandshakeId = handshakeId,
            Commit = commit,
            GrantPub = grantPub,
            PhoneName = phoneName,
            StartedAt = DateTimeOffset.UtcNow,
            Stage = 1,
            Ephemeral = ephemeral,
            Nd = nD,
            EphPubD = ephPubD,
        };
        lock (_gate)
        {
            if (!ReferenceEquals(_anonRoom, room) || room.Active is not null) return;
            room.Active = handshake;
            room.HandshakesStarted++;
        }
        SendControlToPeer(peer, new JsonObject
        {
            ["v"] = 2, ["ctl"] = "pair2_offer", ["roomId"] = AnonText.ToBase64Url(roomId),
            ["handshakeId"] = AnonText.ToBase64Url(handshakeId),
            ["ephPubD"] = AnonText.ToBase64Url(ephPubD), ["nD"] = AnonText.ToBase64Url(nD),
            ["devicePub"] = AnonText.ToBase64Url(identity.PublicKey),
            ["deviceSig"] = AnonText.ToBase64Url(signature),
        });
    }

    private void AnonHandleReveal(string peer, JsonObject body)
    {
        var roomId = B64uField(body, "roomId", 16);
        var handshakeId = B64uField(body, "handshakeId", 16);
        var ephPubP = B64uField(body, "ephPubP", 91);
        var nP = B64uField(body, "nP", 32);
        AnonPairingHandshake handshake;
        AnonPairingRoom room;
        string code;
        lock (_gate)
        {
            if (_anonRoom is not { Expired: false } current) return;
            room = current;
            if (room.Active is not { } active || active.Peer != peer || active.Stage != 1) return;
            handshake = active;
            code = room.Code;
        }
        if (roomId is null || handshakeId is null || ephPubP is null || nP is null
            || !handshake.RoomId.AsSpan().SequenceEqual(roomId)
            || !handshake.HandshakeId.AsSpan().SequenceEqual(handshakeId)
            || !AnonCrypto.IsP256Spki(ephPubP))
        {
            AnonCloseRoom("bad_reveal");
            return;
        }
        var identity = _anonApi!.Identity;
        var recomputed = AnonPairingV2.Commit(identity.DeviceId, roomId, code, handshakeId,
            handshake.GrantPub, handshake.PhoneName, ephPubP, nP);
        if (!AnonText.FixedTimeEqual(recomputed, handshake.Commit))
        {
            AnonCloseRoom("bad_commit");
            return;
        }
        byte[] z;
        AnonPairingV2.MasterKeys keys;
        byte[] transcript;
        string sas;
        try
        {
            z = AnonCrypto.EcdhRaw(handshake.Ephemeral!, ephPubP);
            transcript = AnonPairingV2.Transcript(identity.DeviceId, roomId, code, handshakeId, handshake.Commit,
                handshake.GrantPub, handshake.PhoneName, ephPubP, nP, handshake.EphPubD!, handshake.Nd!,
                identity.PublicKey);
            keys = AnonPairingV2.DeriveMaster(z, transcript);
            sas = AnonPairingV2.SasCode(keys.SessionAuth, transcript);
        }
        catch (Exception)
        {
            AnonCloseRoom("handshake_failed");
            return;
        }
        lock (_gate)
        {
            if (!ReferenceEquals(_anonRoom, room) || room.Active != handshake) return;
            handshake.Np = nP;
            handshake.EphPubP = ephPubP;
            handshake.Keys = keys;
            handshake.Transcript = transcript;
            handshake.Stage = 2;
            room.Confirm = new AnonymousConfirmRequest(handshake.PhoneName, sas, DateTimeOffset.UtcNow + ConfirmTimeout);
        }
        AnonymousPairingChanged?.Invoke();
    }

    private void AnonCloseRoom(string reason)
    {
        string? peer = null;
        lock (_gate)
        {
            if (_anonRoom is { } room)
            {
                peer = room.Active?.Peer;
                _anonRoom = null;
            }
        }
        if (peer is not null) SendControlToPeer(peer, ErrorFrame(reason));
        AnonymousPairingChanged?.Invoke();
        _ = CloseServerRoomAsync();
    }

    /// <summary>Sweeps stale states; called every second from the state tick.</summary>
    private void AnonSweep()
    {
        List<string> stale = [];
        string? timedOutPeer = null;
        bool confirmExpired = false;
        lock (_gate)
        {
            foreach (var (peer, session) in _anonymousSessions)
                if (!session.Ready && DateTimeOffset.UtcNow - session.EstablishedAt > PendingSessionTimeout)
                    stale.Add(peer);
            if (_anonRoom is { } room)
            {
                if (room.Active is { } active && DateTimeOffset.UtcNow - active.StartedAt > HandshakeTimeout)
                {
                    timedOutPeer = active.Peer;
                    room.Active = null;
                }
                if (room.Confirm is { } confirm && DateTimeOffset.UtcNow > confirm.Deadline)
                {
                    room.Confirm = null;
                    room.Active = null;
                    confirmExpired = true;
                }
            }
        }
        foreach (var peer in stale) AnonDropSession(peer);
        if (timedOutPeer is not null) SendControlToPeer(timedOutPeer, ErrorFrame("timeout"));
        if (confirmExpired) _ = ConfirmAnonymousPairingAsync(false);
    }

    private void AnonDropSession(string peer)
    {
        lock (_gate) _anonymousSessions.Remove(peer);
        ShedPeer(peer);
    }

    /// <summary>Asks the relay to close one phone and stops producing frames for it.</summary>
    private void ShedPeer(string peer)
    {
        lock (_gate)
        {
            _subscriptions.Remove(peer);
            _outboxDroppedPeers.Add(peer);
            WriteRelayDrop(_anonLink, peer);
        }
    }

    private JsonObject? AnonSealForPeer(string peer, JsonObject body)
    {
        lock (_gate)
        {
            if (!_anonymousSessions.TryGetValue(peer, out var session) || !session.Ready) return null;
            lock (session.SendLock) return session.Desktop.Seal(body);
        }
    }

    private void AnonPeerGone(string peer)
    {
        bool hadSession;
        lock (_gate) hadSession = _anonymousSessions.Remove(peer);
        // A phone that removed this computer leaves the same way it goes
        // offline; ask the server which pairings still stand.
        if (hadSession) _ = DropRemovedPhonesAsync();
    }

    private int _checkingGrants;

    private async Task DropRemovedPhonesAsync()
    {
        if (_anonApi is null || Interlocked.Exchange(ref _checkingGrants, 1) == 1) return;
        try
        {
            // Only pairings active before asking: one finishing meanwhile is not on the reply yet.
            var active = _phones.AnonymousList().Where(p => p.State == PairedPhoneStore.Active).Select(p => p.PairId).ToList();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var grants = await _anonApi.GrantListAsync(timeout.Token).ConfigureAwait(false);
            var onServer = grants.Select(g => g["pairId"]?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var pairId in active.Where(id => !onServer.Contains(id))) _phones.Remove(pairId);
        }
        catch (Exception ex) { Log($"anon grants: {ex.Message}"); }
        finally { Volatile.Write(ref _checkingGrants, 0); }
    }

    /// <summary>Sends an unsealed control frame (pairing channel, session offer).</summary>
    private void SendControlToPeer(string peer, JsonObject? body)
    {
        if (body is null || peer.Length == 0) return;
        SendFrame(peer, body);
    }

    private static byte[]? B64uField(JsonObject body, string name, int length)
    {
        var text = Text(body, name);
        if (text is null) return null;
        try
        {
            var bytes = AnonText.FromBase64Url(text);
            return bytes.Length == length ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    // ---- encrypted offline directory --------------------------------------------

    /// <summary>Seals the list for the encrypted directory, busy flags and pending
    /// approvals included: the phone's notifications read them. While anything is
    /// busy or waiting, the blob is re-sent every <see cref="AnonDirectoryHeartbeat"/>
    /// so its server timestamp tells the phone this computer is still up.</summary>
    private async Task PublishAnonymousDirectoryAsync(JsonArray listed, CancellationToken ct)
    {
        if (_anonApi is null) return;
        var active = listed.OfType<JsonObject>().Any(row =>
            row["busy"]?.GetValue<bool>() == true || row["pendingApprovals"]?.GetValue<int>() > 0);
        var plain = new JsonObject
        {
            ["machineId"] = _anonApi.Identity.DeviceId,
            ["machineName"] = _machineName,
            ["appVersion"] = _appVersion,
            ["conversations"] = listed.DeepClone(),
        };
        var json = plain.ToJsonString();
        var age = DateTimeOffset.UtcNow - _anonPublishedAt;
        if (json == _anonLink.LastPublished && (!active || age < AnonDirectoryHeartbeat))
        {
            if (active) ScheduleList(AnonDirectoryHeartbeat - age);
            return;
        }
        var nonce = RandomNumberGenerator.GetBytes(12);
        var (ciphertext, tag) = AnonCrypto.Seal(_anonApi.Identity.DirectoryKeyGetOrCreate(), nonce, [], Encoding.UTF8.GetBytes(json));
        await _anonApi.DirectoryPutAsync(new JsonObject
        {
            ["v"] = 1, ["nonce"] = AnonText.ToBase64Url(nonce),
            ["ct"] = AnonText.ToBase64Url(ciphertext), ["tag"] = AnonText.ToBase64Url(tag),
        }, ct).ConfigureAwait(false);
        _anonLink.LastPublished = json;
        _anonPublishedAt = DateTimeOffset.UtcNow;
        if (active) ScheduleList(AnonDirectoryHeartbeat);
    }

    /// <summary>Keep in step with the phone's freshness window (twice this).</summary>
    private static readonly TimeSpan AnonDirectoryHeartbeat = TimeSpan.FromMinutes(3);
    private DateTimeOffset _anonPublishedAt;

    // ---- state holders ------------------------------------------------------------

    private sealed class AnonPairingRoom
    {
        public required string Code { get; init; }
        public required byte[] RoomId { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public int HandshakesStarted;
        public AnonPairingHandshake? Active;
        public AnonymousConfirmRequest? Confirm;
        public bool Expired => DateTimeOffset.UtcNow >= ExpiresAt;
    }

    private sealed class AnonPairingHandshake
    {
        public required string Peer { get; init; }
        public required byte[] RoomId { get; init; }
        public required byte[] HandshakeId { get; init; }
        public required byte[] Commit { get; init; }
        public required byte[] GrantPub { get; init; }
        public required string PhoneName { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public required int Stage { get; set; }
        public required ECDiffieHellman Ephemeral { get; init; }
        public required byte[] Nd { get; init; }
        public required byte[] EphPubD { get; init; }
        public byte[]? Np { get; set; }
        public byte[]? EphPubP { get; set; }
        public AnonPairingV2.MasterKeys? Keys { get; set; }
        public byte[]? Transcript { get; set; }
    }

    private sealed class AnonPeerSession
    {
        public required string Peer { get; init; }
        public required string PairId { get; init; }
        public required byte[] PairIdBytes { get; init; }
        public required byte[] Master { get; init; }
        public required AnonSessionV2.SessionKeys Keys { get; init; }
        public required AnonSessionV2.Direction Phone { get; init; }
        public required AnonSessionV2.Direction Desktop { get; init; }
        public required DateTimeOffset EstablishedAt { get; init; }
        public bool Ready;
        public object SendLock { get; } = new();
    }
}
