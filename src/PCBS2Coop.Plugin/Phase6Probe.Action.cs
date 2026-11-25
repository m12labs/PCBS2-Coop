using UnityEngine;

namespace PCBS2Coop;

/// <summary>Bounded input substitute for isolated tests when desktop input cannot hold a button or target a locked cursor.
/// This leaves hit testing, long presses, screw movement and state transitions to the game.</summary>
internal sealed partial class Phase6Probe
{
    private void WorkView(string command)
    {
        RequireGestureWorld();
        var controller = GameController.Get();
        var work = controller?.GetCurrentState()?.TryCast<WorkingOnComputerStateBase>()
            ?? throw new InvalidOperationException("Work view requires a native work or part-edit state.");
        var bits = command.Split(':');
        if (bits.Length != 3 || !float.TryParse(bits[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var yaw) ||
            !float.TryParse(bits[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pitch) ||
            !float.IsFinite(yaw) || !float.IsFinite(pitch) || Math.Abs(yaw) > 360 || Math.Abs(pitch) > 89)
            throw new ArgumentException("work-view:<yaw -360..360>:<pitch -89..89>");
        var orbit = work.m_orbitCamera ?? work.m_camera?.GetComponent<CameraOrbit>();
        if (orbit == null && controller.m_previousState != null)
            foreach (var previous in controller.m_previousState)
                if (previous?.TryCast<WorkingOnComputerStateBase>() is { } outer)
                    orbit ??= outer.m_orbitCamera ?? outer.m_camera?.GetComponent<CameraOrbit>();
        if (orbit == null) throw new InvalidOperationException("This work state has no native orbit camera.");
        orbit.ResetThisCamera(yaw, pitch);
        orbit.UpdateTransform();
        Event("work-view", new { yaw, pitch, camera = orbit.Camera?.name, physicalRotateInputVerified = false });
    }

    private IntPtr _actionState;
    private IntPtr _actionButton;
    private float _actionUntil;
    private int _actionFirstFrame = -1, _actionUpFrame = -1;

    private void HoldAction(string text)
    {
        RequireGestureWorld();
        var state = GameController.Get()?.GetCurrentState();
        if (state == null || (state.TryCast<WorkingOnComputerState>() == null &&
            state.TryCast<InstallingPartState>() == null && state.TryCast<SimpleScrewState>() == null))
            throw new InvalidOperationException("Action hold requires the native work, part edit or screw state.");
        BeginInputHold(PCBSInput.m_action, "action", text, "action-hold");
    }

    private void HoldInput(string command)
    {
        RequireGestureWorld();
        var bits = command.Split(':');
        if (bits.Length != 3) throw new ArgumentException("input-hold:<action|secondary|menu|inventory|power|tablet|exit>:<seconds>");
        var button = bits[1] switch
        {
            "action" => PCBSInput.m_action, "secondary" => PCBSInput.m_secondaryAction,
            "menu" => PCBSInput.m_menu, "inventory" => PCBSInput.m_inventory,
            "power" => PCBSInput.m_power, "tablet" => PCBSInput.m_toggleTablet, "exit" => PCBSInput.m_exit,
            _ => throw new ArgumentException("Unknown native input button.")
        };
        BeginInputHold(button, bits[1], bits[2], "input-hold");
    }

    private void BeginInputHold(InputButton button, string name, string text, string eventName)
    {
        var state = GameController.Get()?.GetCurrentState();
        if (state == null || button == null) throw new InvalidOperationException("The native state or input button is unavailable.");
        if (!float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var seconds) || !float.IsFinite(seconds) || seconds <= 0 || seconds > 10)
            throw new ArgumentException("action-hold:<seconds>, greater than zero and at most ten.");
        _actionState = state.Pointer;
        _actionButton = button.Pointer;
        _actionUntil = Time.unscaledTime + seconds;
        _actionFirstFrame = Time.frameCount + 1;
        _actionUpFrame = -1;
        Event(eventName, new { button = name, state = state.GetIl2CppType().Name, seconds,
            pointer = Input.mousePosition.ToString(), physicalHoldVerified = false });
    }

    internal static bool ActionInput(InputButton button, string method, out bool value)
    {
        value = false;
        var probe = _current;
        if (probe == null || probe._actionState == IntPtr.Zero || !probe.Safe || button == null ||
            button.Pointer != probe._actionButton) return false;
        var state = GameController.Get()?.GetCurrentState();
        if (state == null || state.Pointer != probe._actionState)
        { probe._actionState = IntPtr.Zero; return false; }
        var frame = Time.frameCount;
        if (frame < probe._actionFirstFrame) return true;
        if (Time.unscaledTime >= probe._actionUntil)
        {
            if (probe._actionUpFrame < 0) probe._actionUpFrame = frame;
            if (frame > probe._actionUpFrame) { probe._actionState = IntPtr.Zero; return false; }
            value = method == nameof(InputButton.IsUp);
            return true;
        }
        value = method == nameof(InputButton.IsHeld) ||
            (method is nameof(InputButton.IsDown) or nameof(InputButton.ConsumeDown) && frame == probe._actionFirstFrame);
        return true;
    }

    // Walking interactions consume the edge directly through Rewired, rather than calling IsDown.
    // This hook is inert unless the isolated probe has a matching, state-scoped input hold.
    [HarmonyLib.HarmonyPatch(typeof(InputButton), nameof(InputButton.ConsumeDown))]
    internal static class ConsumeInputGuard
    {
        private static bool Prefix(InputButton __instance, ref bool __result)
        {
            if (NativeProjection.Active || !ActionInput(__instance, nameof(InputButton.ConsumeDown), out var value)) return true;
            __result = value;
            return false;
        }
    }
}
