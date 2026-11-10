using System.IO.Compression;
using HarmonyLib;
using PCBS2Coop.Network;
using EmailList = Il2CppSystem.Collections.Generic.List<Email>;

namespace PCBS2Coop;

/// <summary>
/// Phase 17e (owner decisions, November 8 and 10, 2026): the shared inbox's saved organization. Either player reads,
/// flags, tags, deletes or restores an e-mail or a job, or makes a new tag; read marks are shared too. The acting game
/// does it at once (the game's own code) and sends it (<see cref="InboxAction"/>); the host's game repeats it on its
/// own inbox, and its next inbox update brings both games in line. Selection, scroll and the open folder stay local.
/// An e-mail the guest's tablet tutorial makes (the theme offers) goes to the host's inbox instead of the guest's.
/// </summary>
internal sealed partial class NativeJobs
{
    private const int MaxMailBytes = 64 * 1024;
    private readonly Queue<InboxAction> _inboxForGuest = new();
    private InboxAction _lastInbox;
    private float _lastInboxAt;
    /// <summary>The guest's tablet tutorial is running: an e-mail it adds goes to the host.</summary>
    [ThreadStatic] internal static bool TutorialMail;

    private static EmailList[] MailLists(CareerStatus.State d) =>
        new[] { d.m_messages, d.m_todaysMessages, d.m_deletedMsgs, d.m_flaggedMsgs, d.m_spamMessages };

    /// <summary>Where an e-mail is: the first of the game's message lists holding it, its place there and its time stamp.</summary>
    private static InboxAction MailRef(InboxAct act, Email mail, string tag = "")
    {
        var d = Data;
        if (d == null || mail == null) return null;
        var lists = MailLists(d);
        for (byte l = 0; l < lists.Length; l++)
        {
            var list = lists[l];
            if (list == null) continue;
            for (var i = 0; i < list.Count; i++)
                if (list[i]?.Pointer == mail.Pointer) return InboxAction.ForMail(act, l, i, mail.m_creationTime, tag);
        }
        return null;
    }

    /// <summary>Host: the e-mail an action names. The time stamp decides; the place picks between equal stamps.</summary>
    private static Email FindMail(InboxAction a)
    {
        var d = Data;
        if (d == null || a.List >= InboxAction.MaxLists) return null;
        var list = MailLists(d)[a.List];
        if (list == null) return null;
        if (a.Index < list.Count && list[a.Index] is { } exact && exact.m_creationTime == a.Time) return exact;
        foreach (var e in list) if (e != null && e.m_creationTime == a.Time) return e;
        return null;
    }

    private static Tag FindTag(string name)
    {
        var tags = Data?.m_emailTags;
        if (tags == null) return null;
        foreach (var t in tags) if (t != null && t.m_tagName == name) return t;
        return null;
    }

    private static Tag TagOn(Il2CppSystem.Collections.Generic.List<Tag> tags, string name)
    {
        if (tags == null) return null;
        foreach (var t in tags) if (t != null && t.m_tagName == name) return t;
        return null;
    }

    /// <summary>A tag list as a fingerprint fragment (names, not just how many).</summary>
    private static string TagNames(Il2CppSystem.Collections.Generic.List<Tag> tags)
    {
        if (tags == null) return "-";
        var names = new List<string>();
        foreach (var t in tags) names.Add(t?.m_tagName ?? "");
        return string.Join(",", names);
    }

    // ---------------- the acting game ----------------

    /// <summary>Whether this game's inbox change is the player's own (not a projection, the host running the guest's
    /// action, or a mirrored click), in a shared career.</summary>
    internal bool OwnInboxChange => NativeCareer.InCareer && !NativeProjection.Active && !HostRunning && !NativeDesktopInput.Injecting && Active;

    /// <summary>Guest: send this game's inbox change to the host. The host's own changes reach the guest in its update.</summary>
    internal void SendInbox(InboxAction action)
    {
        if (action == null || _world.Local == ActorSlot.Host || !OwnInboxChange) return;
        // A multi-job's delete can reach the base job's too: one action, once.
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (_lastInbox is { } last && now - _lastInboxAt < 0.25f && last.Act == action.Act && last.Job == action.Job && last.List == action.List &&
            last.Index == action.Index && last.Time == action.Time && last.Tag == action.Tag) return;
        _lastInbox = action; _lastInboxAt = now;
        if (!_world.Submit(CommandTypes.Jobs, JobWorld.Inbox(Epoch, action), out _))
            Plugin.Logger.LogWarning($"[jobs] inbox {action.Act} not sent (not connected); the host's next update puts it back.");
        else Plugin.Logger.LogInfo($"[jobs] sent inbox {action.Act} ({(action.OnJob ? "job " + action.Job : $"list {action.List} #{action.Index}")}{(action.Tag.Length > 0 ? " tag " + action.Tag : "")}).");
    }

    internal void SendMail(InboxAct act, Email mail, string tag = "") { if (_world.Local != ActorSlot.Host && OwnInboxChange) SendInbox(MailRef(act, mail, tag)); }
    internal void SendJob(InboxAct act, Job job, string tag = "") { if (job != null && job.m_id > 0) SendInbox(InboxAction.ForJob(act, job.m_id, tag)); }

    /// <summary>Guest: an e-mail its tablet tutorial made goes to the host's inbox (serialized, as the inbox update is).</summary>
    internal bool DeliverToHost(Email mail)
    {
        if (_world.Local == ActorSlot.Host || !OwnInboxChange || mail == null) return false;
        byte[] data;
        try { data = PackMail(mail); }
        catch (Exception e) { Plugin.Logger.LogWarning("[jobs] the tutorial's e-mail could not be packed: " + e.Message); return false; }
        if (data.Length > InboxAction.MaxMail) { Plugin.Logger.LogWarning("[jobs] the tutorial's e-mail is too large to send; it stays on this game."); return false; }
        SendInbox(InboxAction.Delivered(data));
        return true;
    }

    private static byte[] PackMail(Email mail)
    {
        byte[] raw;
        var stream = new Il2CppSystem.IO.MemoryStream();
        try
        {
            new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(stream, mail);
            raw = stream.ToArray();
        }
        finally { stream.Dispose(); }
        using var output = new MemoryStream();
        using (var z = new DeflateStream(output, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private static Email UnpackMail(byte[] data)
    {
        using var input = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        input.CopyTo(raw);
        if (raw.Length > MaxMailBytes) throw new InvalidDataException("the e-mail is too large");
        var stream = new Il2CppSystem.IO.MemoryStream(raw.ToArray());
        try
        {
            var obj = new Il2CppSystem.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(stream);
            return obj?.TryCast<Email>() ?? throw new InvalidDataException("that isn't an e-mail");
        }
        finally { stream.Dispose(); }
    }

    // ---------------- host ----------------

    private string CheckInbox(InboxAction a)
    {
        try
        {
            switch (a.Act)
            {
                case InboxAct.CreateTag: return null;
                case InboxAct.Deliver: return null;
            }
            if (a.OnJob) { if (FindJob(a.Job) == null) return "That job isn't on the host's list (any more)."; }
            else if (FindMail(a) == null) return "That e-mail isn't in the host's inbox (any more).";
            if (a.Act == InboxAct.AddTag && FindTag(a.Tag) == null) return "The host's inbox has no tag of that name.";
            return null;
        }
        catch (Exception e) { return "The inbox could not be checked: " + e.Message; }
    }

    public void InboxActed(in MutationContext ctx, InboxAction action)
    {
        if (_world.Local != ActorSlot.Host || ctx.Op.Actor == ActorSlot.Host) return;
        _inboxForGuest.Enqueue(action);
    }

    private void RunInboxQueue()
    {
        var any = false;
        while (_inboxForGuest.Count > 0)
        {
            var a = _inboxForGuest.Dequeue();
            try { RunInboxForGuest(a); any = true; }
            catch (Exception e) { Plugin.Logger.LogWarning($"[jobs] the client's inbox {a.Act} could not be repeated here: {e.Message}"); }
        }
        if (any) Observe(force: false);
    }

    private void RunInboxForGuest(InboxAction a)
    {
        var status = Status ?? throw new InvalidDataException("the career isn't loaded");
        HostRunning = true;
        try
        {
            if (a.Act == InboxAct.CreateTag)
            {
                if (FindTag(a.Tag) != null) return;
                var c = a.Color;
                status.AddTag(a.Tag, ((c >> 24) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, false);
                return;
            }
            if (a.Act == InboxAct.Deliver) { status.AddEmailMessage(UnpackMail(a.Mail), false); return; }
            if (a.OnJob)
            {
                var job = FindJob(a.Job) ?? throw new InvalidDataException($"job {a.Job} is gone");
                switch (a.Act)
                {
                    case InboxAct.Read: if (job.m_status == Job.Status.NEW) job.OnRead(); break;
                    case InboxAct.Flag: if (!job.m_flagged) job.OnFlagAction(); break;
                    case InboxAct.Unflag: if (job.m_flagged) job.OnRemoveFlagAction(); break;
                    case InboxAct.AddTag: if (TagOn(job.m_tags, a.Tag) == null && FindTag(a.Tag) is { } tag) job.AddTag(tag); break;
                    case InboxAct.RemoveTag: if (TagOn(job.m_tags, a.Tag) is { } on) job.RemoveTag(on); break;
                    // Each only from the state the player saw, so a repeated delete never erases.
                    case InboxAct.Delete: if (job.m_status is Job.Status.COLLECTED or Job.Status.REJECTED) job.DoDiscard(); break;
                    case InboxAct.Erase: if (job.m_status == Job.Status.DELETED) job.DoRemove(); break;
                }
                return;
            }
            var mail = FindMail(a) ?? throw new InvalidDataException("the e-mail is gone");
            switch (a.Act)
            {
                case InboxAct.Read: if (mail.m_unread) mail.OnRead(); break;
                case InboxAct.Flag: if (!mail.m_flagged) mail.OnFlagAction(); break;
                case InboxAct.Unflag: if (mail.m_flagged) mail.OnRemoveFlagAction(); break;
                case InboxAct.AddTag: if (TagOn(mail.m_tags, a.Tag) == null && FindTag(a.Tag) is { } tag) mail.AddTag(tag); break;
                case InboxAct.RemoveTag: if (TagOn(mail.m_tags, a.Tag) is { } on) mail.RemoveTag(on); break;
                // Email.OnNegativeAction deletes into Deleted, or erases an e-mail already there.
                case InboxAct.Delete: if (!mail.m_removed) mail.OnNegativeAction(); break;
                case InboxAct.Erase: if (mail.m_removed) mail.OnNegativeAction(); break;
                case InboxAct.Restore: if (mail.m_removed) mail.Restore(); break;
            }
        }
        finally { HostRunning = false; }
        Plugin.Logger.LogInfo($"[jobs] repeated the client's inbox {a.Act}.");
    }
}

/// <summary>
/// Phase 17e hooks: the player's own inbox changes on this game are sent (<see cref="NativeJobs.SendInbox"/>). Every
/// hooked native function is distinct (decompile RVAs: EmailApp.OnClickRow 901100; Email OnFlagAction E40F20,
/// OnRemoveFlagAction E40FD0, AddTag E40E40, RemoveTag E41190, Remove E41210, Restore E41330;
/// CareerStatus.RemoveEmailMessagePermanent D25DC0, AddTag D1ADD0, AddEmailMessage(Email) D18C80; Job OnRead E4D2D0,
/// OnFlagAction E4C650, OnRemoveFlagAction E4D410, AddTag E42950, RemoveTag E4D7A0, DoDiscard E44500, DoRemove E446A0;
/// MultiJob DoDiscard E63130, DoRemove E63320; TabletOnboarding.OnTabletTickState 85B140). <c>Email.OnRead</c> shares its
/// code with other methods (731CC0), so a read e-mail is seen after <c>OnClickRow</c>; <c>Email.OnNegativeAction</c>
/// shares its code with another e-mail type, so a delete is seen at <c>Remove</c> and the permanent delete.
/// </summary>
internal static class InboxHooks
{
    private static NativeJobs Jobs => NativeJobs.Current;
    private static bool Guest => CoopSaves.Current == CoopSaves.Mode.Guest;

    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) { Plugin.Logger.LogError("[jobs] inbox hook failed: " + e); }
    }

    /// <summary>A mirrored click on the other player's desktop never organizes this game's inbox.</summary>
    private static bool NotMirrored() => !NativeCareer.InCareer || NativeProjection.Active || NativeJobs.HostRunning || !NativeDesktopInput.Injecting;

    [HarmonyPatch(typeof(EmailApp), nameof(EmailApp.OnClickRow))]
    internal static class ClickRow
    {
        // A mirrored row click still opens the e-mail on the watching game (its screen follows); only the acting game sends.
        private static bool Prefix(IEmail Row, out bool __state)
        {
            __state = false;
            try { __state = Row?.TryCast<Email>() is { } e && e.m_unread; } catch { }
            return true;
        }

        private static void Postfix(IEmail Row, bool __state)
        {
            if (!__state || !Guest) return;
            Safe(() => { if (Row?.TryCast<Email>() is { } e && !e.m_unread) Jobs?.SendMail(InboxAct.Read, e); });
        }
    }

    private static bool Mail(Email e, InboxAct act, string tag = "")
    {
        if (!NotMirrored()) return false;
        if (Guest) Safe(() => Jobs?.SendMail(act, e, tag));
        return true;
    }

    private static bool JobChange(Job j, InboxAct act, string tag = "")
    {
        if (!NotMirrored()) return false;
        if (Guest) Safe(() => Jobs?.SendJob(act, j, tag));
        return true;
    }

    [HarmonyPatch(typeof(Email), nameof(Email.OnFlagAction))]
    internal static class MailFlag { private static bool Prefix(Email __instance) => __instance.m_flagged || Mail(__instance, InboxAct.Flag); }

    [HarmonyPatch(typeof(Email), nameof(Email.OnRemoveFlagAction))]
    internal static class MailUnflag { private static bool Prefix(Email __instance) => !__instance.m_flagged || Mail(__instance, InboxAct.Unflag); }

    [HarmonyPatch(typeof(Email), nameof(Email.AddTag))]
    internal static class MailTag { private static bool Prefix(Email __instance, Tag tag) => tag == null || Mail(__instance, InboxAct.AddTag, tag.m_tagName ?? ""); }

    [HarmonyPatch(typeof(Email), nameof(Email.RemoveTag))]
    internal static class MailUntag { private static bool Prefix(Email __instance, Tag tag) => tag == null || Mail(__instance, InboxAct.RemoveTag, tag.m_tagName ?? ""); }

    /// <summary>Deleting (into Deleted), from the e-mail's Delete button.</summary>
    [HarmonyPatch(typeof(Email), "Remove")]
    internal static class MailDelete { private static bool Prefix(Email __instance) => __instance.m_removed || Mail(__instance, InboxAct.Delete); }

    /// <summary>Deleting an e-mail already in Deleted removes it for good.</summary>
    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.RemoveEmailMessagePermanent))]
    internal static class MailErase { private static bool Prefix(Email msg) => msg == null || Mail(msg, InboxAct.Erase); }

    [HarmonyPatch(typeof(Email), "Restore")]
    internal static class MailRestore { private static bool Prefix(Email __instance) => !__instance.m_removed || Mail(__instance, InboxAct.Restore); }

    [HarmonyPatch(typeof(Job), nameof(Job.OnRead))]
    internal static class JobRead { private static bool Prefix(Job __instance) => __instance.m_status != Job.Status.NEW || JobChange(__instance, InboxAct.Read); }

    [HarmonyPatch(typeof(Job), nameof(Job.OnFlagAction))]
    internal static class JobFlag { private static bool Prefix(Job __instance) => __instance.m_flagged || JobChange(__instance, InboxAct.Flag); }

    [HarmonyPatch(typeof(Job), nameof(Job.OnRemoveFlagAction))]
    internal static class JobUnflag { private static bool Prefix(Job __instance) => !__instance.m_flagged || JobChange(__instance, InboxAct.Unflag); }

    [HarmonyPatch(typeof(Job), nameof(Job.AddTag))]
    internal static class JobTag { private static bool Prefix(Job __instance, Tag tag) => tag == null || JobChange(__instance, InboxAct.AddTag, tag.m_tagName ?? ""); }

    [HarmonyPatch(typeof(Job), nameof(Job.RemoveTag))]
    internal static class JobUntag { private static bool Prefix(Job __instance, Tag tag) => tag == null || JobChange(__instance, InboxAct.RemoveTag, tag.m_tagName ?? ""); }

    /// <summary>Deleting a collected or turned-down job (into Deleted).</summary>
    [HarmonyPatch(typeof(Job), "DoDiscard")]
    internal static class JobDiscard { private static bool Prefix(Job __instance) => JobChange(__instance, InboxAct.Delete); }

    /// <summary>Deleting a job already in Deleted removes it for good.</summary>
    [HarmonyPatch(typeof(Job), "DoRemove")]
    internal static class JobErase { private static bool Prefix(Job __instance) => JobChange(__instance, InboxAct.Erase); }

    [HarmonyPatch(typeof(MultiJob), "DoDiscard")]
    internal static class MultiJobDiscard { private static bool Prefix(MultiJob __instance) => JobChange(__instance, InboxAct.Delete); }

    [HarmonyPatch(typeof(MultiJob), "DoRemove")]
    internal static class MultiJobErase { private static bool Prefix(MultiJob __instance) => JobChange(__instance, InboxAct.Erase); }

    /// <summary>A new tag made in the e-mail app.</summary>
    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.AddTag))]
    internal static class NewTag
    {
        private static bool Prefix(string tagName, float colorR, float coloG, float colorB, float colorA, bool coreTag)
        {
            if (!NotMirrored()) return false;
            if (!Guest || coreTag || string.IsNullOrEmpty(tagName) || tagName.Length > InboxAction.MaxTag) return true;
            static uint B(float v) => (uint)Math.Clamp((int)Math.Round(v * 255f), 0, 255);
            Safe(() => Jobs?.SendInbox(InboxAction.NewTag(tagName, (B(colorR) << 24) | (B(coloG) << 16) | (B(colorB) << 8) | B(colorA))));
            return true;
        }
    }

    /// <summary>The tablet tutorial's theme offers: on the guest they go to the host's inbox.</summary>
    [HarmonyPatch(typeof(TabletOnboarding), nameof(TabletOnboarding.OnTabletTickState))]
    internal static class TabletTutorial
    {
        private static void Prefix() => NativeJobs.TutorialMail = NativeCareer.InCareer && Guest;
        private static void Finalizer() => NativeJobs.TutorialMail = false;
    }

    [HarmonyPatch(typeof(CareerStatus), nameof(CareerStatus.AddEmailMessage), typeof(Email), typeof(bool))]
    internal static class TutorialMail
    {
        private static bool Prefix(Email msg)
        {
            if (!NativeJobs.TutorialMail || NativeProjection.Active) return true;
            var sent = false;
            Safe(() => sent = Jobs?.DeliverToHost(msg) == true);
            if (sent) Plugin.Logger.LogInfo("[jobs] the tablet tutorial's e-mail goes to the host's inbox.");
            return !sent;
        }
    }

    internal static void Apply(Harmony h)
    {
        foreach (var t in new[] { typeof(ClickRow), typeof(MailFlag), typeof(MailUnflag), typeof(MailTag), typeof(MailUntag), typeof(MailDelete), typeof(MailErase),
                     typeof(MailRestore), typeof(JobRead), typeof(JobFlag), typeof(JobUnflag), typeof(JobTag), typeof(JobUntag), typeof(JobDiscard), typeof(JobErase),
                     typeof(MultiJobDiscard), typeof(MultiJobErase), typeof(NewTag), typeof(TabletTutorial), typeof(TutorialMail) })
            h.PatchAll(t);
    }
}
