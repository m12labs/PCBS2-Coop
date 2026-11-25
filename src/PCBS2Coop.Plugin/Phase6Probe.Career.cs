using UnityEngine;
using Object = UnityEngine.Object;

namespace PCBS2Coop;

/// <summary>Read-only observations for the isolated Career acceptance sweep.</summary>
internal sealed partial class Phase6Probe
{
    private void CareerReport()
    {
        var data = CareerStatus.GetState();
        var controller = GameController.Get();
        var states = new List<State> { controller?.GetCurrentState() };
        if (controller?.m_previousState != null)
            foreach (var previous in controller.m_previousState) states.Add(previous);
        object[] Targets<T>() where T : Component => Object.FindObjectsOfType<T>()
            .OrderBy(x => DesktopPath(x.transform, null)).Select(x => (object)new
            {
                path = DesktopPath(x.transform, null), position = x.transform.position.ToString(),
                active = x.gameObject.activeInHierarchy
            }).ToArray();
        Event("career-report", new
        {
            career = NativeCareer.InCareer,
            tabletUnlocked = data?.m_unlockedTablet,
            doors = Object.FindObjectsOfType<WorkshopDoor>(true).OrderBy(x => DesktopPath(x.transform, null)).Select(d => new
            { path = DesktopPath(d.transform, null), type = d.m_type.ToString(), active = d.gameObject.activeInHierarchy }).ToArray(),
            dayScreens = Object.FindObjectsOfType<DaySummary>(true).Select(d => new
            { path = DesktopPath(d.transform, null), active = d.gameObject.activeInHierarchy,
                endVisible = d.m_endButton?.gameObject.activeInHierarchy, endEnabled = d.m_endButton?.interactable,
                startVisible = d.m_startButton?.gameObject.activeInHierarchy, startEnabled = d.m_startButton?.interactable }).ToArray(),

            cash = data?.m_cash, kudos = data?.m_kudos,
            day = CareerStatus.Get()?.GetCalendar()?.GetToday(),
            jobsDigest = NativeJobs.NativeDigest(), ledgerDigest = NativeCareer.NativeLedgerDigest(),
            jobsFingerprint = NativeJobs.NativeFingerprint(),
            ledgerEntries = data?.m_transactions?.ToArray().Where(t => t != null).Select(t => new
            { price = t.m_price, ticks = t.m_dateTime.Ticks, category = (int)t.m_transactionCategory,
                resale = t.m_resaleValue, name = t.m_name, components = t.m_componentList, collected = t.m_collected,
                rating = t.m_bongleRating, caseSku = t.m_save?.caseID?.GetPartId(),
                wire = Convert.ToBase64String(NativeCareer.WriteEntry(t)) }).ToArray(),

            utilityJobs = JobTutorialHintUi.utilityFunctions?.ToArray().Where(j => j != null).Select(j => new
            { type = j.GetIl2CppType().FullName, running = j.isInProgress,
                installProgress = j.TryCast<PCBS2.Onboarding.Utility.InstallPartClass>()?.progress.ToString() }).ToArray(),
            selectedItem = controller?._selectedItem == null ? null : new
            { sku = controller._selectedItem.GetPartId(), pointer = controller._selectedItem.Pointer.ToInt64(),
                owned = CoopRunner.Instance?.Coop.Career?.IdOf(controller._selectedItem) ?? 0 },
            dayReady = CoopRunner.Instance?.Coop.Day?.Handler.HasReady,
            state = GameController.Get()?.GetCurrentState()?.GetIl2CppType().Name,
            player = LocalPlayer.Find(), remotePlayer = LocalPlayer.Find(forRemote: true),
            remoteView = CoopRunner.Instance?.Coop.RemoteViewDiagnostic,
            install = InstallReport(),
            workStates = states.Where(s => s != null).Select(s => new
            {
                type = s.GetIl2CppType().Name, pointer = s.Pointer.ToInt64(),
                standing = s.TryCast<WorkingOnComputerStateBase>()?.m_lastPlayerPosition.ToString()
            }).ToArray(),
            tutorials = JobTutorialHintUi.onboardingClasses?.ToArray().Where(t => t != null).Select(t => new
            {
                type = t.GetIl2CppType().FullName, state = (int)t.State, progress = t.getOnboardingProgress
            }).ToArray(),
            inventory = CareerStatus.Get()?.GetInventory()?.ToArray().Where(p => p != null).Select(p => p.GetPartId()).ToArray(),
            peripheralInventory = data?.m_peripheralInventory?.ToArray().Where(p => p != null).Select(p => p.GetPartId()).ToArray(),
            deliveries = data?.m_deliveries?.ToArray().Where(p => p != null).Select(p => p.GetPartId()).ToArray(),
            notes = Targets<InteractableNote>(), deliveryBoxes = Targets<DeliveryBox>(), packages = Targets<Package>(),
            benches = Object.FindObjectOfType<SaveLoadSystem>()?.benchSlots.Select((b, index) => new
            {
                index, path = b == null ? null : DesktopPath(b.transform, null),
                pc = b == null ? 0UL : CoopRunner.Instance.Coop.Objects.IdOf(b.GetCaseInSlot()).Value
            }).ToArray()
        });
        CoopRunner.Instance?.Coop.Onboarding?.ReportNow();
    }

    private object InstallReport()
    {
        var install = GameController.Get()?.GetCurrentState()?.TryCast<InstallingPartState>();
        if (install == null) return null;
        object Read(string name) => typeof(InstallingPartState).GetProperty(name, _gesturePrivateInstance)?.GetValue(install);
        var camera = install.m_camera ?? GameController.Get()?.GetActiveCamera() ?? Camera.main;
        object ScreenPoint(Vector3 position)
        {
            if (camera == null) return null;
            var p = camera.WorldToScreenPoint(position);
            return new { x = p.x, y = Screen.height - p.y, depth = p.z };
        }
        object[] Screws(string name)
        {
            var list = Read(name) as Il2CppSystem.Collections.Generic.List<Screw>;
            return list == null ? Array.Empty<object>() : list.ToArray().Where(s => s != null).Select(s => (object)new
            {
                name = s.name, position = s.transform.position.ToString(),
                viewport = camera == null ? null : camera.WorldToViewportPoint(s.transform.position).ToString(),
                clientPixel = ScreenPoint(s.transform.position),
                rotation = s.m_rotation, offset = s.m_offset, active = s.gameObject.activeInHierarchy
            }).ToArray();
        }
        return new
        {
            camera = camera?.name, cameraPosition = camera?.transform.position.ToString(),
            accept = Read("m_accept"),
            screws = Screws("m_screws"), done = Screws("m_doneScrews"),
            standoffs = Screws("m_standOffs"), doneStandoffs = Screws("m_doneStandOffs")
        };
    }
}
