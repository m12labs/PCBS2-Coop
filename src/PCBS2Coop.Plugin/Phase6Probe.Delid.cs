using UnityEngine;

namespace PCBS2Coop;

internal sealed partial class Phase6Probe
{
    private IntPtr _delidActionState;
    private float _delidActionUntil;
    private int _delidActionStage;

    private object DelidField(DeliddingState state, string name) =>
        (typeof(DeliddingState).GetProperty(name, _gesturePrivateInstance) ?? throw new MissingMemberException(nameof(DeliddingState), name))
        .GetValue(state);

    private bool DelidProbeCommand(string command)
    {
        if (command is not ("delid-report" or "delid-hold-action")) return false;
        if (!Safe) return true;
        try
        {
            var state = GameController.Get()?.GetCurrentState()?.TryCast<DeliddingState>()
                ?? throw new InvalidOperationException("Open the game's CPU delidding tool first.");
            var stage = DelidField(state, "_delidFlowState");
            if (command == "delid-hold-action")
            {
                var number = Convert.ToInt32(stage);
                if (number is not (6 or 15 or 17 or 37))
                    throw new InvalidOperationException("The native tool must be at the heatgun or screw hold step.");
                _delidActionState = state.Pointer;
                _delidActionStage = number;
                _delidActionUntil = Time.unscaledTime + 8f;
                Event("delid-hold-action", new { stage = stage.ToString(), seconds = 8, bypasses = "physical Action hold only" });
                return true;
            }

            var cpu = DelidField(state, "CPUPart") as PartInstance;
            var tool = DelidField(state, "DeliddingTool") as DelidTool;
            var camera = DelidField(state, "BenchCamera") as Camera;
            var selected = DelidField(state, "selected") as ISelectable;
            var candidates = DelidField(state, "controllerInteract") as Il2CppSystem.Collections.Generic.List<ISelectable>;
            Event("delid-report", new
            {
                stage = stage.ToString(), cpu = cpu?.GetPartId(), variant = PartVariants.Of(cpu),
                heat = DelidField(state, "heatAmount"),
                heatGun = tool?.HeatGunBone?.transform.localPosition.ToString(),
                selected = selected?.TryCast<Component>()?.name,
                candidates = candidates?.ToArray().Where(item => item != null).Select(item =>
                {
                    var component = item.TryCast<Component>();
                    if (component == null || camera == null) return null;
                    var collider = component.GetComponentInChildren<Collider>();
                    var point = camera.WorldToScreenPoint(collider == null ? component.transform.position : collider.bounds.center);
                    return new { component.name, x = Mathf.RoundToInt(point.x), y = Mathf.RoundToInt(Screen.height - point.y), visible = point.z > 0 };
                }).Where(item => item != null).ToArray()
            });
        }
        catch (Exception e) { Event("delid-probe-error", e.ToString()); }
        return true;
    }

    internal static bool DelidActionHeld(InputButton button)
    {
        var probe = _current;
        if (probe == null || probe._delidActionUntil <= 0 || Time.unscaledTime >= probe._delidActionUntil ||
            button == null || button.Pointer != PCBSInput.m_action?.Pointer || !probe.Safe) return false;
        var state = GameController.Get()?.GetCurrentState()?.TryCast<DeliddingState>();
        return state != null && state.Pointer == probe._delidActionState &&
            Convert.ToInt32(probe.DelidField(state, "_delidFlowState")) == probe._delidActionStage;
    }
}
