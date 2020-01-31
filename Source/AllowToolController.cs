﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AllowTool.Context;
using AllowTool.Settings;
using HugsLib;
using HugsLib.Settings;
using HugsLib.Utils;
using RimWorld;
using UnityEngine;
using Verse;

namespace AllowTool {
	/// <summary>
	/// The hub of the mod. 
	/// Injects the custom designators and handles hotkey presses.
	/// </summary>
	[EarlyInit]
	public class AllowToolController : ModBase {
		internal const string DesignatorHandleNamePrefix = "show";
		internal const string ReverseDesignatorHandleNamePrefix = "showrev";

		public static FieldInfo GizmoGridGizmoListField;
		public static FieldInfo DraftControllerAutoUndrafterField;
		public static FieldInfo DesignatorHasDesignateAllFloatMenuOptionField;
		public static MethodInfo DesignatorGetDesignationMethod;
		public static MethodInfo DesignatorGetRightClickFloatMenuOptionsMethod;
		public static MethodInfo DesignationCategoryDefResolveDesignatorsMethod;
		public static AllowToolController Instance { get; private set; }

		// called before implied def generation
		public static void BeforeImpliedDefGeneration() {
			try {
				// setting handles bust be created after language data is loaded
				// and before DesignationCategoryDef.ResolveDesignators is called
				// implied def generation is a good loading stage to do that on
				Instance.PrepareSettingsHandles();

				if (!Instance.HaulWorktypeSetting) {
					AllowToolDefOf.HaulingUrgent.visible = false;
				}
				if (Instance.FinishOffWorktypeSetting) {
					AllowToolDefOf.FinishingOff.visible = true;
				}
			} catch (Exception e) {
				Log.Error("Error during early setting handle setup: "+e);
			}
		}

		private readonly List<DesignatorEntry> activeDesignators = new List<DesignatorEntry>();
		private readonly Dictionary<string, SettingHandle<bool>> designatorToggleHandles = new Dictionary<string, SettingHandle<bool>>();
		private readonly Dictionary<string, SettingHandle<bool>> reverseDesignatorToggleHandles = new Dictionary<string, SettingHandle<bool>>();
		private SettingHandle<bool> settingGlobalHotkeys;
		private bool expandToolSettings;
		private bool expandProviderSettings;
		private bool expandReverseToolSettings;
		private bool dependencyRefreshScheduled;
		
		public override string ModIdentifier {
			get { return "AllowTool"; }
		}

		// needed to access protected field from static getter below
		private ModLogger GetLogger {
			get { return base.Logger; }
		}
		internal new static ModLogger Logger {
			get { return Instance.GetLogger; }
		}

		internal SettingHandle<int> SelectionLimitSetting { get; private set; }
		internal SettingHandle<bool> ContextOverlaySetting { get; private set; }
		internal SettingHandle<bool> ContextWatermarkSetting { get; private set; }
		internal SettingHandle<bool> ReplaceIconsSetting { get; private set; }
		internal SettingHandle<bool> HaulWorktypeSetting { get; private set; }
		internal SettingHandle<bool> FinishOffWorktypeSetting { get; private set; }
		internal SettingHandle<bool> ExtendedContextActionSetting { get; private set; }
		internal SettingHandle<bool> ReverseDesignatorPickSetting { get; private set; }
		internal SettingHandle<bool> FinishOffSkillRequirement { get; private set; }
		internal SettingHandle<bool> FinishOffUnforbidsSetting { get; private set; }
		internal SettingHandle<bool> PartyHuntSetting { get; private set; }
		internal SettingHandle<bool> PartyHuntFinishSetting { get; private set; }
		internal SettingHandle<bool> PartyHuntDesignatedSetting { get; private set; }
		internal SettingHandle<bool> StorageSpaceAlertSetting { get; private set; }

		public UnlimitedDesignationDragger Dragger { get; private set; }
		public WorldSettings WorldSettings { get; private set; }

		private AllowToolController() {
			Instance = this;
		}

		public override void EarlyInitalize() {
			Dragger = new UnlimitedDesignationDragger();
			PrepareReflection();
			Compat_PickUpAndHaul.Apply();
		}

		public override void Update() {
			Dragger.Update();
			if (Time.frameCount % (60*60) == 0) { // 'bout every minute
				DesignatorContextMenuController.CheckForMemoryLeak();
			}
		}

		public override void Tick(int currentTick) {
			DesignationCleanupManager.Tick(currentTick);
		}

		public override void OnGUI() {
			if (Current.Game == null || Current.Game.CurrentMap == null) return;
			var selectedDesignator = Find.MapUI.designatorManager.SelectedDesignator;
			for (int i = 0; i < activeDesignators.Count; i++) {
				var designator = activeDesignators[i].designator;
				if (selectedDesignator != designator) continue;
				designator.SelectedOnGUI();
			}
			if (Event.current.type == EventType.KeyDown) {
				CheckForHotkeyPresses();
			}
		}

		public override void WorldLoaded() {
			WorldSettings = UtilityWorldObjectManager.GetUtilityWorldObject<WorldSettings>();
		}

		public override void MapLoaded(Map map) {
			// necessary when adding the mod to existing saves
			var injected = AllowToolUtility.EnsureAllColonistsKnowAllWorkTypes(map);
			if (injected) {
				AllowToolUtility.EnsureAllColonistsHaveWorkTypeEnabled(AllowToolDefOf.HaulingUrgent, map);
				AllowToolUtility.EnsureAllColonistsHaveWorkTypeEnabled(AllowToolDefOf.FinishingOff, map);
			}
		}

		public override void SettingsChanged() {
			ResolveAllDesignationCategories();
		}

		public bool IsDesignatorEnabledInSettings(ThingDesignatorDef def) {
			return GetToolHandleSettingValue(designatorToggleHandles, DesignatorHandleNamePrefix + def.defName);
		}

		public bool IsReverseDesignatorEnabledInSettings(ReverseDesignatorDef def) {
			return GetToolHandleSettingValue(reverseDesignatorToggleHandles, ReverseDesignatorHandleNamePrefix + def.defName);
		}

		public Designator_SelectableThings TryGetDesignator(ThingDesignatorDef def) {
			return activeDesignators.Select(e => e.designator).FirstOrDefault(d => d.Def == def);
		}

		private void PrepareSettingsHandles() {
			settingGlobalHotkeys = Settings.GetHandle("globalHotkeys", "setting_globalHotkeys_label".Translate(), "setting_globalHotkeys_desc".Translate(), true);
			ContextOverlaySetting = Settings.GetHandle("contextOverlay", "setting_contextOverlay_label".Translate(), "setting_contextOverlay_desc".Translate(), true);
			ContextWatermarkSetting = Settings.GetHandle("contextWatermark", "setting_contextWatermark_label".Translate(), "setting_contextWatermark_desc".Translate(), true);
			ReplaceIconsSetting = Settings.GetHandle("replaceIcons", "setting_replaceIcons_label".Translate(), "setting_replaceIcons_desc".Translate(), true);
			HaulWorktypeSetting = Settings.GetHandle("haulUrgentlyWorktype", "setting_haulUrgentlyWorktype_label".Translate(), "setting_haulUrgentlyWorktype_desc".Translate(), true);
			FinishOffWorktypeSetting = Settings.GetHandle("finishOffWorktype", "setting_finishOffWorktype_label".Translate(), "setting_finishOffWorktype_desc".Translate(), false);
			ExtendedContextActionSetting = Settings.GetHandle("extendedContextActionKey", "setting_extendedContextHotkey_label".Translate(), "setting_extendedContextHotkey_desc".Translate(), true);
			ReverseDesignatorPickSetting = Settings.GetHandle("reverseDesignatorPick", "setting_reverseDesignatorPick_label".Translate(), "setting_reverseDesignatorPick_desc".Translate(), true);
			FinishOffUnforbidsSetting = Settings.GetHandle("finishOffUnforbids", "setting_finishOffUnforbids_label".Translate(), "setting_finishOffUnforbids_desc".Translate(), true);
			
			// party hunt
			PartyHuntSetting = Settings.GetHandle("partyHunt", "setting_partyHunt_label".Translate(), "setting_partyHunt_desc".Translate(), true);
			PartyHuntFinishSetting = Settings.GetHandle("partyHuntFinish", "setting_partyHuntFinish_label".Translate(), null, true);
			PartyHuntDesignatedSetting = Settings.GetHandle("partyHuntDesignated", "setting_partyHuntDesignated_label".Translate(), null, false);
			PartyHuntFinishSetting.VisibilityPredicate = PartyHuntDesignatedSetting.VisibilityPredicate = () => false;

			StorageSpaceAlertSetting = Settings.GetHandle("storageSpaceAlert", "setting_storageSpaceAlert_label".Translate(), "setting_storageSpaceAlert_desc".Translate(), true);
			
			SelectionLimitSetting = Settings.GetHandle("selectionLimit", "setting_selectionLimit_label".Translate(), "setting_selectionLimit_desc".Translate(), 200, Validators.IntRangeValidator(50, 100000));
			SelectionLimitSetting.SpinnerIncrement = 50;
			// designators
			MakeSettingsCategoryToggle("setting_showToolToggles_label", () => expandToolSettings = !expandToolSettings);
			foreach (var designatorDef in DefDatabase<ThingDesignatorDef>.AllDefs) {
				var handleName = DesignatorHandleNamePrefix + designatorDef.defName;
				var handle = Settings.GetHandle(handleName, "setting_showTool_label".Translate(designatorDef.label), null, true);
				handle.VisibilityPredicate = () => expandToolSettings;
				designatorToggleHandles[handleName] = handle;
			}
			// context menus
			MakeSettingsCategoryToggle("setting_showProviderToggles_label", () => expandProviderSettings = !expandProviderSettings);
			SettingHandle.ShouldDisplay menuEntryHandleVisibility = () => expandProviderSettings;
			foreach (var handle in DesignatorContextMenuController.RegisterMenuEntryHandles(Settings)) {
				handle.VisibilityPredicate = menuEntryHandleVisibility;
			}
			// reverse designators
			MakeSettingsCategoryToggle("setting_showReverseToggles_label", () => expandReverseToolSettings = !expandReverseToolSettings);
			foreach (var reverseDef in DefDatabase<ReverseDesignatorDef>.AllDefs) {
				var handleName = ReverseDesignatorHandleNamePrefix + reverseDef.defName;
				var handle = Settings.GetHandle(handleName, "setting_showTool_label".Translate(reverseDef.designatorDef.label), "setting_reverseDesignator_desc".Translate(), true);
				handle.VisibilityPredicate = () => expandReverseToolSettings;
				reverseDesignatorToggleHandles[handleName] = handle;
			}
			FinishOffSkillRequirement = Settings.GetHandle("finishOffSkill", "setting_finishOffSkill_label".Translate(), "setting_finishOffSkill_desc".Translate(), true);
			FinishOffSkillRequirement.VisibilityPredicate = () => Prefs.DevMode;
		}

		private void MakeSettingsCategoryToggle(string labelId, Action buttonAction) {
			var toolToggle = Settings.GetHandle<bool>(labelId, labelId.Translate(), null);
			toolToggle.Unsaved = true;
			toolToggle.CustomDrawer = rect => {
				if (Widgets.ButtonText(rect, "setting_showToggles_btn".Translate())) buttonAction();
				return false;
			};
		}

		internal void InjectDuringResolveDesignators() {
			ScheduleDesignatorDependencyRefresh();
		}

		internal void ScheduleDesignatorDependencyRefresh() {
			if (dependencyRefreshScheduled) return;
			dependencyRefreshScheduled = true;
			activeDesignators.Clear();
			// push the job to the next frame to avoid repeating this for every category as the game loads
			HugsLibController.Instance.DoLater.DoNextUpdate(() => {
				try {
					dependencyRefreshScheduled = false;
					var resolvedDesignators = AllowToolUtility.GetAllResolvedDesignators().ToArray();
					foreach (var designator in resolvedDesignators.OfType<Designator_SelectableThings>()) {
						activeDesignators.Add(new DesignatorEntry(designator, designator.Def.hotkeyDef));
					}
					DesignatorContextMenuController.RebindAllContextMenus();
				} catch (Exception e) {
					Logger.Error($"Error during designator dependency refresh: {e}");
				}
			});
		}

		private void PrepareReflection() {
			var gizmoGridType = GenTypes.GetTypeInAnyAssemblyNew("InspectGizmoGrid", "RimWorld");
			if (gizmoGridType != null) {
				GizmoGridGizmoListField = gizmoGridType.GetField("gizmoList", HugsLibUtility.AllBindingFlags);
			}
			DesignatorGetDesignationMethod = typeof(Designator).GetMethod("get_Designation", HugsLibUtility.AllBindingFlags);
			DesignatorHasDesignateAllFloatMenuOptionField = typeof(Designator).GetField("hasDesignateAllFloatMenuOption", HugsLibUtility.AllBindingFlags);
			DesignatorGetRightClickFloatMenuOptionsMethod = typeof(Designator).GetMethod("get_RightClickFloatMenuOptions", HugsLibUtility.AllBindingFlags);
			DraftControllerAutoUndrafterField = typeof(Pawn_DraftController).GetField("autoUndrafter", HugsLibUtility.AllBindingFlags);
			DesignationCategoryDefResolveDesignatorsMethod = typeof(DesignationCategoryDef).GetMethod("ResolveDesignators", HugsLibUtility.AllBindingFlags);
			if (GizmoGridGizmoListField == null || GizmoGridGizmoListField.FieldType != typeof(List<Gizmo>)
				|| DesignatorGetDesignationMethod == null || DesignatorGetDesignationMethod.ReturnType != typeof(DesignationDef)
				|| DesignatorHasDesignateAllFloatMenuOptionField == null || DesignatorHasDesignateAllFloatMenuOptionField.FieldType != typeof(bool)
				|| DesignatorGetRightClickFloatMenuOptionsMethod == null || DesignatorGetRightClickFloatMenuOptionsMethod.ReturnType != typeof(IEnumerable<FloatMenuOption>)
				|| DraftControllerAutoUndrafterField == null || DraftControllerAutoUndrafterField.FieldType != typeof(AutoUndrafter)
				|| DesignationCategoryDefResolveDesignatorsMethod == null
				) {
				Logger.Error("Failed to reflect required members");
			}
		}

		private void CheckForHotkeyPresses() {
			if (Event.current.keyCode == KeyCode.None) return;
			if (AllowToolDefOf.ToolContextMenuAction.JustPressed) {
				DesignatorContextMenuController.ProcessContextActionHotkeyPress();
			}
			if (!settingGlobalHotkeys || Find.CurrentMap == null) return;
			for (int i = 0; i < activeDesignators.Count; i++) {
				var entry = activeDesignators[i];
				if(entry.key == null || !entry.key.JustPressed || !entry.designator.Visible) continue;
				Find.DesignatorManager.Select(entry.designator);
				break;
			}
		}

		private class DesignatorEntry {
			public readonly Designator_SelectableThings designator;
			public readonly KeyBindingDef key;
			public DesignatorEntry(Designator_SelectableThings designator, KeyBindingDef key) {
				this.designator = designator;
				this.key = key;
			}
		}

		private bool GetToolHandleSettingValue(Dictionary<string, SettingHandle<bool>> handleDict, string handleName) {
			return handleDict.TryGetValue(handleName, out SettingHandle<bool> handle) && handle.Value;
		}

		private void ResolveAllDesignationCategories() {
			foreach (var categoryDef in DefDatabase<DesignationCategoryDef>.AllDefs) {
				DesignationCategoryDefResolveDesignatorsMethod.Invoke(categoryDef, new object[0]);
			}
		}
	}
}