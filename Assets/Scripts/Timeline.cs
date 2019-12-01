﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DG.Tweening;
using Michsky.UI.ModernUIPack;
using Melanchall.DryWetMidi.Smf.Interaction;
using NotReaper.Grid;
using NotReaper.IO;
using NotReaper.Managers;
using NotReaper.Models;
using NotReaper.Targets;
using NotReaper.Tools;
using NotReaper.Tools.ChainBuilder;
using NotReaper.UI;
using NotReaper.UserInput;
using SFB;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Application = UnityEngine.Application;
using NotReaper.Timing;


namespace NotReaper {


	public class Timeline : MonoBehaviour {

		//Hidden public values
		[HideInInspector] public static AudicaFile audicaFile;

		[HideInInspector] public static SongDesc desc;

		[Header("Audio Stuff")]

		[SerializeField] private AudioSource aud;
		[SerializeField] private AudioSource previewAud;
		[SerializeField] private AudioSource leftSustainAud;
		[SerializeField] private AudioSource rightSustainAud;
		[SerializeField] private Transform spectrogram;

		[Header("UI Elements")]
		[SerializeField] private MiniTimeline miniTimeline;
		[SerializeField] private TextMeshProUGUI songTimestamp;
		[SerializeField] private TextMeshProUGUI curTick;
		[SerializeField] private TextMeshProUGUI curDiffText;

		[SerializeField] private HorizontalSelector beatSnapSelector;

		[Header("Prefabs")]
		public TargetIcon timelineTargetIconPrefab;
		public TargetIcon gridTargetIconPrefab;
		public GameObject BPM_MarkerPrefab;

		[Header("Extras")]
		[SerializeField] private NRDiscordPresence nrDiscordPresence;
		[SerializeField] private DifficultyManager difficultyManager;
		[SerializeField] public EditorToolkit Tools;
		[SerializeField] private Transform timelineTransformParent;
		[SerializeField] private Transform gridTransformParent;
		public static Transform gridNotesStatic;
		public static Transform timelineNotesStatic;
		[SerializeField] private Renderer timelineBG;
		
		public Slider musicVolumeSlider;

		[Header("Configuration")]
		public float playbackSpeed = 1f;

		public float musicVolume = 0.5f; 
		public float sustainVolume = 0.5f;
		public float previewDuration = 0.1f;

		//Target Lists
		public List<Target> notes;
		public static List<Target> orderedNotes;
		public List<Target> selectedNotes;

		public static List<Target> loadedNotes;

		public static bool inTimingMode = false;
		public static bool audioLoaded = false;
		public static bool audicaLoaded = false;

		private Color leftColor;
		private Color rightColor;
		private Color bothColor;
		private Color neitherColor;

		private static readonly int MainTex = Shader.PropertyToID("_MainTex");
		
		/// <summary>
		/// The current time in the song
		/// </summary>
		/// <value></value>
		public static QNT_Timestamp time { get; set; }

		public int beatSnap { get; private set; } = 4;

		[HideInInspector] public static int scale = 20;
		public static float scaleTransform;
		private float targetScale = 0.7f;
		private float scaleOffset = 0;
		private static Relative_QNT offset = new Relative_QNT(0);

		/// <summary>
		/// If the timeline is currently being moved by an animation.
		/// </summary>
		private bool animatingTimeline = false;

		[HideInInspector] public bool hover = false;
		public bool paused = true;


		public Button applyButtonTiming;
		public Button generateAudicaButton;
		public Button loadAudioFileTiming;

		public List<TempoChange> tempoChanges = new List<TempoChange>();
		private List<GameObject> bpmMarkerObjects = new List<GameObject>();

		//Tools
		private void Start() {

			//Load the config file
			NRSettings.LoadSettingsJson();


			notes = new List<Target>();
			orderedNotes = new List<Target>();
			loadedNotes = new List<Target>();
			selectedNotes = new List<Target>();
			
			gridNotesStatic = gridTransformParent;
			timelineNotesStatic = timelineTransformParent;

			NRSettings.OnLoad(() => {
				sustainVolume = NRSettings.config.sustainVol;
				musicVolume = NRSettings.config.mainVol;
				musicVolumeSlider.value = musicVolume;

				if (NRSettings.config.clearCacheOnStartup) {
					HandleCache.ClearCache();
				}
				
				SetAudioDSP();
			});

			musicVolumeSlider.onValueChanged.AddListener(val => {
				musicVolume = val;
				NRSettings.config.mainVol = musicVolume;
				NRSettings.SaveSettingsJson();
			});

			StartCoroutine(CalculateNoteCollidersEnabled());

			Physics.autoSyncTransforms = false;

			ChainBuilder.timeline = this;
		}

		public void UpdateUIColors() {
			curDiffText.color = NRSettings.config.rightColor;
			leftColor = NRSettings.config.leftColor;
			rightColor = NRSettings.config.rightColor;
			bothColor = UserPrefsManager.bothColor;
			neitherColor = UserPrefsManager.neitherColor;
		}

		void OnApplicationQuit() {
			//DirectoryInfo dir = new DirectoryInfo(Application.persistentDataPath + "\\temp\\");
			//dir.Delete(true);
		}

		public void SortOrderedList() {
			orderedNotes.Sort((left, right) =>  left.data.time.CompareTo(right.data.time));
		}

		public static int BinarySearchOrderedNotes(QNT_Timestamp cueTime)
		{ 
			int min = 0;
			int max = orderedNotes.Count - 1;
				while (min <=max) {
				int mid = (min + max) / 2;
				QNT_Timestamp midCueTime = orderedNotes[mid].data.time;
				if (cueTime == midCueTime) {
					while(mid != 0 && orderedNotes[mid - 1].data.time == cueTime) {
						--mid;
					}
					return mid;
				}
				else if (cueTime < midCueTime) {
					max = mid - 1;
				}
				else {
					min = mid + 1;
				}
			}
			return -1;
		}

		public TargetData FindTargetData(QNT_Timestamp time, TargetBehavior behavior, TargetHandType handType) {
			int idx = BinarySearchOrderedNotes(time);
			if(idx == -1) {
				Debug.LogWarning("Couldn't find note with time " + time);
				return null;
			}

			for(int i = idx; i < orderedNotes.Count; ++i) {
				Target t = orderedNotes[i];
				if (t.data.time == time &&
					t.data.behavior == behavior &&
					t.data.handType == handType) {
					return t.data;
				}
			}

			Debug.LogWarning("Couldn't find note with time " + time + " and index " + idx);
			return null;
		}
		
		public Target FindNote(TargetData data) {
			int idx = BinarySearchOrderedNotes(data.time);
			if(idx == -1) {
				Debug.LogWarning("Couldn't find note with time " + data.time);
				return null;
			}

			for(int i = idx; i < orderedNotes.Count; ++i) {
				Target t = orderedNotes[i];
				if (t.data.ID == data.ID) {
					return t;
				}
			}

			Debug.LogWarning("Couldn't find note with time " + data.time + " and index " + idx);
			return null;
		}

		public List<Target> FindNotes(List<TargetData> targetDataList) {
			List<Target> foundNotes = new List<Target>();
			foreach (TargetData data in targetDataList) {
				foundNotes.Add(FindNote(data));
			}
			return foundNotes;
		}

		//When loading from cues, use this.
		public TargetData GetTargetDataForCue(Cue cue) {
			TargetData data = new TargetData(cue);
			if (data.time.tick == 0) data.time = new QNT_Timestamp(120);
			return data;
		}


		//Use when adding a singular target to the project (from the user)
		public void AddTarget(float x, float y) {
			if(audicaLoaded == false) {
				return;
			}

			TargetData data = new TargetData();
			data.x = x;
			data.y = y;
			data.handType = EditorInput.selectedHand;
			data.behavior = EditorInput.selectedBehavior;

			QNT_Timestamp tempTime = GetClosestBeatSnapped(time);

			foreach (Target target in loadedNotes) {
				if (target.data.time ==  tempTime && (target.data.handType == EditorInput.selectedHand) && (EditorInput.selectedTool != EditorTool.Melee)) return;
			}

			data.time = GetClosestBeatSnapped(time);

			//Default sustains length should be more than 0.
			if (data.supportsBeatLength) {
				data.beatLength = Constants.QuarterNoteDuration;
			} else {
				data.beatLength = new QNT_Duration(Constants.PulsesPerQuarterNote / 2);
			}

			switch (EditorInput.selectedVelocity) {
				case UITargetVelocity.Standard:
					data.velocity = TargetVelocity.Standard;
					break;

				case UITargetVelocity.Snare:
					data.velocity = TargetVelocity.Snare;
					break;

				case UITargetVelocity.Percussion:
					data.velocity = TargetVelocity.Percussion;
					break;

				case UITargetVelocity.ChainStart:
					data.velocity = TargetVelocity.ChainStart;
					break;

				case UITargetVelocity.Chain:
					data.velocity = TargetVelocity.Chain;
					break;

				case UITargetVelocity.Melee:
					data.velocity = TargetVelocity.Melee;
					break;

				default:
					data.velocity = TargetVelocity.Standard;
					break;
			}


			var action = new NRActionAddNote {targetData = data};
			Tools.undoRedoManager.AddAction(action);
		}

		//Adds a target directly to the timeline. targetData is kept as a reference NOT copied
		public Target AddTargetFromAction(TargetData targetData, bool transient = false) {

			var timelineTargetIcon = Instantiate(timelineTargetIconPrefab, timelineTransformParent);
			timelineTargetIcon.location = TargetIconLocation.Timeline;
			var transform1 = timelineTargetIcon.transform;
			transform1.localPosition = new Vector3(targetData.time.ToBeatTime(), 0, 0);

			Vector3 noteScale = transform1.localScale;
			noteScale.x = targetScale;
			transform1.localScale = noteScale;

			var gridTargetIcon = Instantiate(gridTargetIconPrefab, gridTransformParent);
			gridTargetIcon.transform.localPosition = new Vector3(targetData.x, targetData.y, targetData.time.ToBeatTime());
			gridTargetIcon.location = TargetIconLocation.Grid;

			Target target = new Target(targetData, timelineTargetIcon, gridTargetIcon, transient);

			notes.Add(target);
			orderedNotes = notes.OrderBy(v => v.data.time.tick).ToList();

			//Subscribe to the delete note event so we can delete it if the user wants. And other events.
			target.DeleteNoteEvent += DeleteTarget;

			target.TargetEnterLoadedNotesEvent += AddLoadedNote;
			target.TargetExitLoadedNotesEvent += RemoveLoadedNote;

			target.TargetSelectEvent += SelectTarget;
			target.TargetDeselectEvent += DeselectTarget;

			target.MakeTimelineUpdateSustainLengthEvent += UpdateSustainLength;

			//Trigger all callbacks on the note
			targetData.Copy(targetData); 

			//Also generate chains if needed
			if(targetData.behavior == TargetBehavior.NR_Pathbuilder) {
				ChainBuilder.GenerateChainNotes(targetData);
			}

			return target;
		}

		private void UpdateSustains() {
			foreach (var note in loadedNotes) {
				if (note.data.behavior == TargetBehavior.Hold) {
					if ((note.GetRelativeBeatTime() < 0) && (note.GetRelativeBeatTime() + note.data.beatLength.ToBeatTime() > 0))
					{

						var particles = note.GetHoldParticles();
						if (!particles.isEmitting) {
							particles.Play();

							float panPos = (float) (note.data.x / 7.15);
							if (note.data.handType == TargetHandType.Left) {
								leftSustainAud.volume = sustainVolume;
								leftSustainAud.panStereo = panPos;

							} else if (note.data.handType == TargetHandType.Right) {
								rightSustainAud.volume = sustainVolume;
								rightSustainAud.panStereo = panPos;
							}

							var main = particles.main;
							main.startColor = note.data.handType == TargetHandType.Left ? new Color(leftColor.r, leftColor.g, leftColor.b, 1) : new Color(rightColor.r, rightColor.g, rightColor.b, 1);
						}

						ParticleSystem.Particle[] parts = new ParticleSystem.Particle[particles.particleCount];
						particles.GetParticles(parts);

						for (int i = 0; i < particles.particleCount; ++i) {
							parts[i].position = new Vector3(parts[i].position.x, parts[i].position.y, 0);
						}

						particles.SetParticles(parts, particles.particleCount);

					} else
					{
						var particles = note.GetHoldParticles();
						if (particles.isEmitting) {
							particles.Stop();
							if (note.data.handType == TargetHandType.Left) {
								leftSustainAud.volume = 0.0f;
							} else if (note.data.handType == TargetHandType.Right) {
								rightSustainAud.volume = 0.0f;
							}
						}
					}
				}
			}
		}


		public static void AddLoadedNote(Target target) {
			loadedNotes.Add(target);
		}

		public static void RemoveLoadedNote(Target target) {
			loadedNotes.Remove(target);
		}


		public void SelectTarget(Target target) {
			if (!selectedNotes.Contains(target)) {
				selectedNotes.Add(target);
				target.Select();
			}
		}


		public void DeselectTarget(Target target, bool resettingAll = false) {
			if (selectedNotes.Contains(target)) {

				target.Deselect();

				if (!resettingAll) {
					selectedNotes.Remove(target);
				}
			}

		}

		public void DeselectAllTargets() {
			if (!audicaLoaded) return;

			foreach (Target target in selectedNotes) {
				DeselectTarget(target, true);
			}

			selectedNotes = new List<Target>();
		}
		
		/// <summary>
		/// Updates a sustain length from the buttons next to sustains.
		/// </summary>
		/// <param name="target">The target to affect</param>
		/// <param name="increase">If true, increase by one beat snap, if false, the opposite.</param>
		public void UpdateSustainLength(Target target, bool increase) {
			if (!target.data.supportsBeatLength) return;

			QNT_Duration increment = Constants.DurationFromBeatSnap((uint)beatSnap);
			QNT_Duration minimum = Constants.SixteenthNoteDuration;
			
			if (increase) {
				if (target.data.beatLength < increment) target.data.beatLength = new QNT_Duration(0);
				target.data.beatLength += increment;
			} else {
				target.data.beatLength =new QNT_Duration(Math.Max((target.data.beatLength - increment).tick, minimum.tick));
			}

			target.UpdatePath();
		}

		public void MoveGridTargets(List<TargetGridMoveIntent> intents) {
			var action = new NRActionGridMoveNotes();
			action.targetGridMoveIntents = intents.Select(intent => new TargetGridMoveIntent(intent)).ToList();
			Tools.undoRedoManager.AddAction(action);
		}

		public void MoveTimelineTargets(List<TargetTimelineMoveIntent> intents) {
			SortOrderedList();
			var action = new NRActionTimelineMoveNotes();
			action.targetTimelineMoveIntents = intents.Select(intent => new TargetTimelineMoveIntent(intent)).ToList();
			Tools.undoRedoManager.AddAction(action);
		}

		public void PasteCues(List<TargetData> cues, QNT_Timestamp pasteBeatTime) {

			// paste new targets in the original locations
			var targetDataList = cues.Select(copyData => {
				var data = new TargetData(copyData);

				if(data.behavior == TargetBehavior.NR_Pathbuilder) {
					data.pathBuilderData = new PathBuilderData();
					var note = FindNote(copyData);
					if(note != null) {
						data.pathBuilderData.Copy(note.data.pathBuilderData);
					}
				}

				return data;
			}).ToList();

			// find the soonest target in the selection
			QNT_Timestamp earliestTargetBeatTime = new QNT_Timestamp(long.MaxValue);
			foreach (TargetData data in targetDataList) {
				QNT_Timestamp time = data.time;
				if (time < earliestTargetBeatTime) {
					earliestTargetBeatTime = time;
				}
			}

			// shift all by the amount needed to move the earliest note to now
			Relative_QNT diff = pasteBeatTime - earliestTargetBeatTime;
			foreach (TargetData data in targetDataList) {
				data.time += diff;
			}

			var action = new NRActionMultiAddNote();
			action.affectedTargets = targetDataList;
			Tools.undoRedoManager.AddAction(action);

			DeselectAllTargets();
			FindNotes(targetDataList).ForEach(target => SelectTarget(target));
		}

		// Invert the selected targets' colour
		public void SwapTargets(List<Target> targets) {
			var action = new NRActionSwapNoteColors();
			action.affectedTargets = targets.Select(target => target.data).ToList();
			Tools.undoRedoManager.AddAction(action);
		}

		// Flip the selected targets on the grid about the X
		public void FlipTargetsHorizontal(List<Target> targets) {
			var action = new NRActionHFlipNotes();
			action.affectedTargets = targets.Select(target => target.data).ToList();
			Tools.undoRedoManager.AddAction(action);
		}

		// Flip the selected targets on the grid about the Y
		public void FlipTargetsVertical(List<Target> targets) {
			var action = new NRActionVFlipNotes();
			action.affectedTargets = targets.Select(target => target.data).ToList();
			Tools.undoRedoManager.AddAction(action);
		}
		
		public void SetTargetHitsounds(List<TargetSetHitsoundIntent> intents) {
			var action = new NRActionSetTargetHitsound();
			action.targetSetHitsoundIntents = intents.Select(intent => new TargetSetHitsoundIntent(intent)).ToList();
			Tools.undoRedoManager.AddAction(action);
	}

		public void DeleteTarget(Target target) {
			var action = new NRActionRemoveNote();
			action.targetData = target.data;
			Tools.undoRedoManager.AddAction(action);
		}

		public void DeleteTargetFromAction(TargetData targetData) {
			Target target = FindNote(targetData);
			if (target == null) return;

			notes.Remove(target);
			orderedNotes.Remove(target);
			loadedNotes.Remove(target);
			selectedNotes.Remove(target);

			target.Destroy(this);
			target = null;
		}

		public void DeleteTargets(List<Target> targets) {
			var action = new NRActionMultiRemoveNote();
			action.affectedTargets = targets.Select(target => target.data).ToList();
			Tools.undoRedoManager.AddAction(action);
		}

		public void DeleteAllTargets() {
			var notesTemp = notes.ToList();
			foreach (Target target in notesTemp) {
				target.Destroy(this);
			}

			notes = new List<Target>();
			orderedNotes = new List<Target>();
			loadedNotes = new List<Target>();
			selectedNotes = new List<Target>();
		}

		public void ResetTimeline() {
			DeleteAllTargets();
			Tools.undoRedoManager.ClearActions();
			tempoChanges.Clear();
		}


		public void Export()
		{

			Debug.Log("Saving: " + audicaFile.desc.title);
			
			//Ensure all chains are generated
			List<TargetData> nonGeneratedNotes = new List<TargetData>();

			foreach(Target note in notes) {
				if(note.data.behavior == TargetBehavior.NR_Pathbuilder && note.data.pathBuilderData.createdNotes == false) {
					nonGeneratedNotes.Add(note.data);
				}
			}

			foreach(var data in nonGeneratedNotes) {
				ChainBuilder.GenerateChainNotes(data);
			}

			//Export map
			string dirpath = Application.persistentDataPath;

			CueFile export = new CueFile();
			export.cues = new List<Cue>();
			export.NRCueData = new NRCueData();

			foreach (Target target in orderedNotes) {

				if (target.data.beatLength == 0) target.data.beatLength = Constants.SixteenthNoteDuration;
				
				if (target.data.behavior == TargetBehavior.Metronome) continue;
				
				var cue = NotePosCalc.ToCue(target, offset, false);

				if(target.data.behavior == TargetBehavior.NR_Pathbuilder) {
					export.NRCueData.pathBuilderNoteCues.Add(cue);
					export.NRCueData.pathBuilderNoteData.Add(target.data.pathBuilderData);
					continue;
				}

				export.cues.Add(cue);
			}

			switch (difficultyManager.loadedIndex) {
				case 0:
					audicaFile.diffs.expert = export;
					break;
				case 1:
					audicaFile.diffs.advanced = export;
					break;
				case 2:
					audicaFile.diffs.moderate = export;
					break;
				case 3:
					audicaFile.diffs.beginner = export;
					break;
			}

			audicaFile.desc = desc;


			AudicaExporter.ExportToAudicaFile(audicaFile);

			NotificationShower.AddNotifToQueue(new NRNotification("Map saved successfully!"));


		}

		public void ExportAndPlay() {
			Export();
			string songFolder = PathLogic.GetSongFolder();
			File.Delete(Path.Combine(songFolder, audicaFile.desc.songID + ".audica"));
			File.Copy(audicaFile.filepath, Path.Combine(songFolder, audicaFile.desc.songID + ".audica"));

			string newPath = Path.GetFullPath(Path.Combine(songFolder, @"..\..\..\..\"));
			System.Diagnostics.Process.Start(Path.Combine(newPath, "Audica.exe"));
		}


		public void LoadTimingMode(AudioClip clip) {
			if (audicaLoaded) return;

			inTimingMode = true;

			aud.clip = clip;
			previewAud.clip = null;
			leftSustainAud.clip = null;
			rightSustainAud.clip = null;

			audioLoaded = true;
		}

		public void CopyTimestampToClipboard() {
			string timestamp = songTimestamp.text;
			GUIUtility.systemCopyBuffer = "**" + timestamp + "**" + " - ";
		}

		public void SetTimingModeStats(UInt64 microsecondsPerQuarterNote, int tickOffset) {
			DeleteAllTargets();

			SetBPM(new QNT_Timestamp(0), microsecondsPerQuarterNote);

			var cue = new Cue {
				pitch = 40,
				tickLength = 1,
				behavior = TargetBehavior.Metronome,
				velocity = TargetVelocity.Metronome,
				handType = TargetHandType.Either
			};

			for (int i = 0; i < 300; i++) {
				cue.tick = (480 * i) + tickOffset;
				AddTargetFromAction(new TargetData(cue));
			}

			//time = 0;
			SafeSetTime();
		}


		public void ExitTimingMode() {

			inTimingMode = false;
			DeleteAllTargets();

		}

		public bool LoadAudicaFile(bool loadRecent = false, string filePath = null) {

			inTimingMode = false;
			SetOffset(new Relative_QNT(0));

			if (audicaLoaded && NRSettings.config.saveOnLoadNew) {
				Export();
			}

			if (loadRecent) {
				audicaFile = null;
				audicaFile = AudicaHandler.LoadAudicaFile(PlayerPrefs.GetString("recentFile", null));
				if (audicaFile == null) return false;

			} else if (filePath != null) {
				audicaFile = null;
				audicaFile = AudicaHandler.LoadAudicaFile(filePath);
				PlayerPrefs.SetString("recentFile", audicaFile.filepath);

			} else {

				string[] paths = StandaloneFileBrowser.OpenFilePanel("Audica File (Not OST)", Path.Combine(Application.persistentDataPath), "audica", false);

				if (paths.Length == 0) return false;
				
				audicaFile = null;
				
				audicaFile = AudicaHandler.LoadAudicaFile(paths[0]);
				PlayerPrefs.SetString("recentFile", paths[0]);
			}

			ResetTimeline();
			
			desc = audicaFile.desc;
			
			// Get song BPM
			if (audicaFile.song_mid != null) {
				foreach (var tempo in audicaFile.song_mid.GetTempoMap().Tempo) {
					QNT_Timestamp time = new QNT_Timestamp((UInt64)tempo.Time);
					SetBPM(time, (UInt64)tempo.Value.MicrosecondsPerQuarterNote);
				}
			} 

			//Update our discord presence
			nrDiscordPresence.UpdatePresenceSongName(desc.title);


			//Loads all the sounds.
			StartCoroutine(GetAudioClip($"file://{Application.dataPath}/.cache/{audicaFile.desc.cachedMainSong}.ogg"));
			StartCoroutine(LoadLeftSustain($"file://{Application.dataPath}/.cache/{audicaFile.desc.cachedSustainSongLeft}.ogg"));
			StartCoroutine(LoadRightSustain($"file://{Application.dataPath}/.cache/{audicaFile.desc.cachedSustainSongRight}.ogg"));

			//foreach (Cue cue in audicaFile.diffs.expert.cues) {
			//AddTarget(cue);
			//}
			//Difficulty manager loads stuff now
			audicaLoaded = true;
			difficultyManager.LoadHighestDifficulty();

			//Disable timing window buttons so users don't mess stuff up.
			applyButtonTiming.interactable = false;
			generateAudicaButton.interactable = false;
			loadAudioFileTiming.interactable = false;

			

			//Loaded successfully

			NotificationShower.AddNotifToQueue(new NRNotification("Map loaded successfully!"));
			NotificationShower.AddNotifToQueue(new NRNotification("Hold F1 to view shortcuts"));
			return true;
		}


		IEnumerator GetAudioClip(string uri) {
			using(UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.OGGVORBIS)) {
				yield return www.SendWebRequest();

				if (www.isNetworkError) {
					Debug.Log(www.error);
				} else {
					AudioClip myClip = DownloadHandlerAudioClip.GetContent(www);
					aud.clip = myClip;
					previewAud.clip = myClip;
					
					int zeroBPMIndex = GetCurrentBPMIndex(new QNT_Timestamp(0));
					if(zeroBPMIndex == -1) {
						SetBPM(new QNT_Timestamp(0), Constants.MicrosecondsPerQuarterNoteFromBPM(desc.tempo));
					}

					//We modify the list, so we need to copy it
					var cloneList = desc.tempoList.ToList();
					foreach(var tempo in cloneList) {
						SetBPM(tempo.time, tempo.microsecondsPerQuarterNote);
					}
					desc.tempoList = tempoChanges;
					
					audioLoaded = true;
					audicaLoaded = true;
					
					//Load the preview start point
					miniTimeline.SetPreviewStartPoint(ShiftTick(new QNT_Timestamp(0), (float)desc.previewStartSeconds));

					//Difficulty manager loads stuff now
					//difficultyManager.LoadHighestDifficulty(false);
					//SetScale(20);
					//Resources.FindObjectsOfTypeAll<OptionsMenu>().First().Init(bpm, offset, beatSnap, songid, songtitle, songartist, songendevent, songpreroll, songauthor);

					//spectrogram.GetComponentInChildren<AudioWaveformVisualizer>().Init();


				}
			}
		}

		IEnumerator LoadLeftSustain(string uri) {
			Debug.Log("Loading left sustian.");
			using(UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.OGGVORBIS)) {
				yield return www.SendWebRequest();

				if (www.isNetworkError) {
					Debug.Log(www.error);
				} else {
					AudioClip myClip = DownloadHandlerAudioClip.GetContent(www);
					leftSustainAud.clip = myClip;
					leftSustainAud.volume = 0f;
				}
			}
		}
		IEnumerator LoadRightSustain(string uri) {
			using(UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.OGGVORBIS)) {
				yield return www.SendWebRequest();

				if (www.isNetworkError) {
					Debug.Log(www.error);
				} else {
					AudioClip myClip = DownloadHandlerAudioClip.GetContent(www);
					rightSustainAud.clip = myClip;
					rightSustainAud.volume = 0f;

				}
			}
		}
		
		public void SetPlaybackSpeed(float speed) {
			if (!audioLoaded) return;

			playbackSpeed = speed;
			aud.pitch = speed;
			previewAud.pitch = speed;
			leftSustainAud.pitch = speed;
			rightSustainAud.pitch = speed;
		}
		
		public void SetPlaybackSpeedFromSlider(Slider slider) {
			if (!audioLoaded) return;

			playbackSpeed = slider.value;
			aud.pitch = slider.value;
			previewAud.pitch = slider.value;
			leftSustainAud.pitch = slider.value;
			rightSustainAud.pitch = slider.value;
		}

		public void SetBPM(QNT_Timestamp time, UInt64 microsecondsPerQuarterNote) {
			foreach(var bpm in bpmMarkerObjects) {
				Destroy(bpm);
			}
			bpmMarkerObjects.Clear();

			TempoChange c = new TempoChange();
			c.time = time;
			c.microsecondsPerQuarterNote = microsecondsPerQuarterNote;

			bool found = false;
			for(int i = 0; i < tempoChanges.Count; ++i) {
				if(tempoChanges[i].time == time) {
					tempoChanges[i] = c;
					if(microsecondsPerQuarterNote == 0) {
						tempoChanges.RemoveAt(i);
					}
					found = true;
					break;
				}
			}
			
			if(!found && microsecondsPerQuarterNote != 0) {
				tempoChanges.Add(c);
			}
			tempoChanges = tempoChanges.OrderBy(tempo => tempo.time.tick).ToList();
			
			if (desc != null) {
				desc.tempoList = tempoChanges;
			}
			SetScale(scale);

			foreach(var tempo in tempoChanges) {
				var timelineBPM = Instantiate(BPM_MarkerPrefab, timelineTransformParent);
				var transform1 = timelineBPM.transform;
				transform1.localPosition = new Vector3(tempo.time.ToBeatTime(), -0.5f, 0);

				float bpm = Constants.GetBPMFromMicrosecondsPerQuaterNote(tempo.microsecondsPerQuarterNote);
				timelineBPM.GetComponentInChildren<TextMesh>().text = Conversion.MicrosecondsToString(tempo.microsecondsPerQuarterNote);
				bpmMarkerObjects.Add(timelineBPM);
			}

			if(aud.clip == null) {
				return;
			}

			QNT_Timestamp endOfAudio = ShiftTick(new QNT_Timestamp(0), aud.clip.length);
			
			List<Vector3> vertices = new List<Vector3>();
			List<int> indices = new List<int>();

			uint barLengthIncr = 0;
			UInt64 increment = Constants.PulsesPerQuarterNote;
			for(UInt64 t = 0; t < endOfAudio.tick;) {
				int indexStart = vertices.Count;

				const float width = 0.025f;
				const float maxHeight = 1.025f;
				const float zIndex = 3;
				float start = t / (float)Constants.PulsesPerQuarterNote;
				start -= width / 2;

				float height = 0.0f;
				if(barLengthIncr == 0) {
					height = maxHeight;
				}
				else if(barLengthIncr == 2) {
					height = maxHeight / 2;
				}
				else if(barLengthIncr == 1 || barLengthIncr == 3) {
					height = maxHeight / 4;
				}

				vertices.Add(new Vector3(start, -0.5f, zIndex));
				vertices.Add(new Vector3(start + width, -0.5f, zIndex));
				vertices.Add(new Vector3(start + width, -0.5f + height, zIndex));
				vertices.Add(new Vector3(start, -0.5f + height, zIndex));

				indices.Add(indexStart + 0);
				indices.Add(indexStart + 1);
				indices.Add(indexStart + 2);

				indices.Add(indexStart + 2);
				indices.Add(indexStart + 3);
				indices.Add(indexStart + 0);

				barLengthIncr++;
				barLengthIncr = barLengthIncr % 4;
				
				bool newTempo = false;
				foreach(TempoChange tempoChange in tempoChanges) {
					if(t < tempoChange.time.tick && t + increment >= tempoChange.time.tick) {
						barLengthIncr = 0;
						t = tempoChange.time.tick;
						newTempo = true;
						break;
					}
				}

				if(!newTempo) {
					t += increment;
				}
			}

			Mesh mesh = timelineNotesStatic.gameObject.GetComponent<MeshFilter>().mesh;
			mesh.Clear();

			mesh.vertices = vertices.ToArray();
			mesh.triangles = indices.ToArray();

			//List<float> quaterNotePositions = new List<float>();
			//for(int i = 0; i < DurationToBeats(aud.clip.length); ++i) {
			//	float x = BeatsToDuration(0.0f, i, BeatDurationDirection.Forward) - DurationToBeats(t) - (offset / 480f);
			//}
		}

		public void SetOffset(Relative_QNT newOffset) {
			StopCoroutine(AnimateSetTime(new QNT_Timestamp(0)));
			Relative_QNT diff = offset - newOffset;
			offset = newOffset;
			
			QNT_Timestamp newTime = time + diff; //BeatsToDuration(0.0f, diff / 480f, BeatDurationDirection.Forward);
			if (newTime != time) {
				StartCoroutine(AnimateSetTime(newTime));
			}
		}

		public void SetSnap(int newSnap) {
			beatSnap = newSnap;
		}
		public void BeatSnapChanged() {
			string temp = beatSnapSelector.elements[beatSnapSelector.index];
			int snap = 4;
			int.TryParse(temp.Substring(2), out snap);
			beatSnap = snap;
		}

		private int GetCurrentBPMIndex(QNT_Timestamp t) {
			for(int i = 0; i < tempoChanges.Count; ++i) {
				var c = tempoChanges[i];

				if(t >= c.time && (i + 1 >= tempoChanges.Count || t < tempoChanges[i + 1].time)) {
					return i;
				}
			}

			return -1;
		}

		public float GetBpmFromTime(QNT_Timestamp t) {
			int idx = GetCurrentBPMIndex(t);
			if(idx != -1) {
				return Constants.GetBPMFromMicrosecondsPerQuaterNote(tempoChanges[idx].microsecondsPerQuarterNote);
			}
			else {
				return 1.0f;
			}
		}

		public void SetBeatTime(QNT_Timestamp t) {
			float x = t.ToBeatTime() - offset.ToBeatTime();

			timelineBG.material.SetTextureOffset(MainTex, new Vector2((x / 4f + scaleOffset), 1));

			timelineTransformParent.transform.localPosition = Vector3.left * x / (scale / 20f);

			gridTransformParent.transform.localPosition = Vector3.back * x;
		}

		public void SetScale(int newScale) {
			if (newScale < 5 || newScale > 100) return;
			timelineBG.material.SetTextureScale("_MainTex", new Vector2(newScale / 4f, 1));
			scaleOffset = -newScale % 8 / 8f;

			Vector3 timelineTransformScale = timelineTransformParent.transform.localScale;
			timelineTransformScale.x *= (float) scale / newScale;
			scaleTransform = timelineTransformScale.x;
			timelineTransformParent.transform.localScale = timelineTransformScale;

			targetScale *= (float) newScale / scale;
			// fix scaling on all notes
			foreach (Transform note in timelineTransformParent.transform) {
				Vector3 noteScale = note.localScale;
				noteScale.x = targetScale;
				note.localScale = noteScale;
			}


			scale = newScale;

			foreach (Target target in orderedNotes) {
				target.UpdateTimelineSustainLength();
			}
		}

		public void UpdateTrail() {
			Vector3[] positions = new Vector3[gridTransformParent.childCount];
			for (int i = 0; i < gridTransformParent.transform.childCount; i++) {
				positions[i] = gridTransformParent.GetChild(i).localPosition;
			}
			positions = positions.OrderBy(v => v.z).ToArray();
			var liner = gridTransformParent.gameObject.GetComponentInChildren<LineRenderer>();
			liner.positionCount = gridTransformParent.childCount;
			liner.SetPositions(positions);
		}

		IEnumerator CalculateNoteCollidersEnabled() {

			int framesToSplitOver = 50;

			int amtToCalc = Mathf.RoundToInt(orderedNotes.Count / framesToSplitOver);

			int j = 0;

			for (int i = 0; i < framesToSplitOver; i++) {

				while (j < orderedNotes.Count) {
					
					float targetPos = orderedNotes[j].GetRelativeBeatTime();

					if (targetPos > -20 && targetPos < 20) {
						orderedNotes[j].EnableColliders();
					} else {
						orderedNotes[j].EnableColliders();
					}


					if (j > amtToCalc * (i + 1)) break;

					j++;
				}


				yield return null;

			}

			while (j < orderedNotes.Count) {
				float targetPos = orderedNotes[j].GetRelativeBeatTime();

				if (targetPos > -20 && targetPos < 20) {
					orderedNotes[j].EnableGridColliders();
				} else {
					orderedNotes[j].DisableGridColliders();
				}
				j++;
			}
			StartCoroutine(CalculateNoteCollidersEnabled());

		}


		public void EnableNearSustainButtons() {
			foreach (Target target in loadedNotes) {
				if (!target.data.supportsBeatLength) continue;

				bool shouldDisplayButton = paused; //Need to be paused
				shouldDisplayButton &= target.GetRelativeBeatTime() < 2 && target.GetRelativeBeatTime() > -2; //Target needs to be "near"

				//Be in drag select, or be a path builder note in path builder mode
				shouldDisplayButton &= EditorInput.selectedTool == EditorTool.DragSelect || (target.data.behavior == TargetBehavior.NR_Pathbuilder && EditorInput.selectedTool == EditorTool.ChainBuilder);
				
				if (shouldDisplayButton) {
					target.EnableSustainButtons();
				} else {
					target.DisableSustainButtons();
				}
			}
		}

		bool checkForNearSustainsOnThisFrame = false;

		public void Update() {
			
			UpdateSustains();

			if (!paused) {
				time = ShiftTick(new QNT_Timestamp(0), aud.time + Time.unscaledDeltaTime);
			}

			bool isScrollingBeatSnap = false;
			
			
			bool isShiftDown = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
			bool isAltDown = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

			if (hover) {
				if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) {
					if (Input.mouseScrollDelta.y > 0.1f) {
						SetScale(scale - 1);
					} else if (Input.mouseScrollDelta.y < -0.1f) {
						SetScale(scale + 1);
					}
					SetBeatTime(time);
				}
			}

			if (isAltDown && Input.mouseScrollDelta.y < -0.1f) {
				isScrollingBeatSnap = true;
				beatSnapSelector.PreviousClick();

			} else if (isAltDown && Input.mouseScrollDelta.y > 0.11f) {
				isScrollingBeatSnap = true;
				beatSnapSelector.ForwardClick();
			}
			
			//if (!(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) && hover))

			if (!isShiftDown && Input.mouseScrollDelta.y < -0.1f && !isScrollingBeatSnap) {
				if (!audioLoaded) return;
				time = GetClosestBeatSnapped(time + Constants.DurationFromBeatSnap((uint)beatSnap));

				SafeSetTime();
				if (paused) {
					previewAud.Play();
					checkForNearSustainsOnThisFrame = true;
				}

				SetBeatTime(time);

				StopCoroutine(AnimateSetTime(new QNT_Timestamp(0)));


			} else if (!isShiftDown && Input.mouseScrollDelta.y > 0.1f && !isScrollingBeatSnap) {
				if (!audioLoaded) return;
				time = GetClosestBeatSnapped(time - Constants.DurationFromBeatSnap((uint)beatSnap));

				SafeSetTime();
				if (paused) {
					previewAud.Play();
					checkForNearSustainsOnThisFrame = true;
				}

				SetBeatTime(time);

				StopCoroutine(AnimateSetTime(new QNT_Timestamp(0)));

			}

            if (Input.GetKeyDown(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.C)) {
                CopyTimestampToClipboard();
            }

			if (!paused && !animatingTimeline) {
				SetBeatTime(time);
			}
			if (previewAud.time > TimestampToSeconds(time) + previewDuration) {
				previewAud.Pause();
			}

			previewAud.volume = aud.volume = musicVolume;

			SetCurrentTime();
			SetCurrentTick();

			miniTimeline.SetPercentagePlayed(GetPercentagePlayed());


			EnableNearSustainButtons();
		}

		public double GetPercentPlayedFromSeconds(double seconds)
		{
			return seconds / aud.clip.length;
		}


		public void JumpToPercent(float percent) {
			if (!audioLoaded) return;

			time = ShiftTick(new QNT_Timestamp(0), aud.clip.length * percent);

			SafeSetTime();
			SetCurrentTime();
			SetCurrentTick();

			SetBeatTime(time);
		}

		public void JumpToX(float x) {
			StopCoroutine(AnimateSetTime(new QNT_Timestamp(0)));

			float posX = Math.Abs(timelineTransformParent.position.x) + x;
			QNT_Timestamp newTime = new QNT_Timestamp(0) + QNT_Duration.FromBeatTime(posX);
			newTime = GetClosestBeatSnapped(newTime);

			StartCoroutine(AnimateSetTime(newTime)); //@FIX scale / 20?
		}


		public void TogglePlayback() {
			if (!audioLoaded) return;

			if (paused) {
				aud.Play();
				//metro.StartMetronome();

				previewAud.Pause();

				if (leftSustainAud.clip && TimestampToSeconds(time) < leftSustainAud.clip.length) {
					leftSustainAud.Play();
					rightSustainAud.Play();
				}

				paused = false;
			} else {
				aud.Pause();

				if (leftSustainAud.clip != null) {
					leftSustainAud.Pause();
					rightSustainAud.Pause();

				}
				paused = true;

				//Snap to the beat snap when we pause
				time = GetClosestBeatSnapped(time);

				float currentTimeSeconds = TimestampToSeconds(time);
				if (currentTimeSeconds > aud.clip.length) {
					time = ShiftTick(new QNT_Timestamp(0), aud.clip.length);
				}

				SetBeatTime(time);
				SafeSetTime();
				SetCurrentTick();
				SetCurrentTime();
			}
		}

		public void SafeSetTime() {
			if (time.tick < 0) time = new QNT_Timestamp(0);
			if (!audioLoaded) return;

			float currentTimeSeconds = TimestampToSeconds(time);

			if (currentTimeSeconds > aud.clip.length) {
				time = ShiftTick(new QNT_Timestamp(0), aud.clip.length);
				currentTimeSeconds = aud.clip.length;
			}
			aud.time = currentTimeSeconds;
			previewAud.time = currentTimeSeconds;

			float tempTime = currentTimeSeconds;
			if (leftSustainAud.clip && currentTimeSeconds > leftSustainAud.clip.length) {
				tempTime = leftSustainAud.clip.length;
			}
			leftSustainAud.time = tempTime;

			if (rightSustainAud.clip && currentTimeSeconds > rightSustainAud.clip.length) {
				tempTime = rightSustainAud.clip.length;
			}
			rightSustainAud.time = tempTime;
		}

		public IEnumerator AnimateSetTime(QNT_Timestamp newTime) {

			animatingTimeline = true;

			if (!audioLoaded) yield break;

			if (TimestampToSeconds(newTime) > aud.clip.length) {
				newTime = ShiftTick(new QNT_Timestamp(0), aud.clip.length);
			}

			float newTimeInSeconds = TimestampToSeconds(newTime);

			aud.time = newTimeInSeconds;
			previewAud.time = newTimeInSeconds;

			float tempTime = newTimeInSeconds;
			if (leftSustainAud.clip && newTimeInSeconds > leftSustainAud.clip.length) {
				tempTime = leftSustainAud.clip.length;
			}
			leftSustainAud.time = tempTime;

			if (rightSustainAud.clip && newTimeInSeconds > rightSustainAud.clip.length) {
				tempTime = rightSustainAud.clip.length;
			}
			rightSustainAud.time = tempTime;

			//DOTween.Play
			DOTween.To(t => SetBeatTime(new QNT_Timestamp((UInt64)Math.Round(t))), time.tick, newTime.tick, 0.2f).SetEase(Ease.InOutCubic);

			yield return new WaitForSeconds(0.2f);

			time = newTime;
			animatingTimeline = false;

			SafeSetTime();
			SetBeatTime(time);

			SetCurrentTime();
			SetCurrentTick();


			yield break;

		}

		void OnMouseEnter() {
			hover = true;
		}

		void OnMouseExit() {
			hover = false;
		}

		//Snap (rounded down) to the nearest beat given by `beatSnap`
		public QNT_Timestamp GetClosestBeatSnapped(QNT_Timestamp timeToSnap) {
			int tempoIndex = GetCurrentBPMIndex(timeToSnap);
			if(tempoIndex == -1) {
				return QNT_Timestamp.GetSnappedValue(timeToSnap, beatSnap);
			}

			TempoChange currentTempo = tempoChanges[tempoIndex];
			QNT_Duration offsetFromTempoChange = new QNT_Duration(timeToSnap.tick - currentTempo.time.tick);
			offsetFromTempoChange = QNT_Duration.GetSnappedValue(offsetFromTempoChange, beatSnap);
			return currentTempo.time + offsetFromTempoChange;
		}

		private void OnMouseDown() {
			//We don't want to interfere with drag select
			if (EditorInput.selectedTool == EditorTool.DragSelect) return;
			JumpToX(Camera.main.ScreenToWorldPoint(Input.mousePosition).x);
		}

		public float GetPercentagePlayed() {
			if (aud.clip)
				return (TimestampToSeconds(time) / aud.clip.length);

			else
				return 0;
		}

		//Shifts `startTime` by `duration` seconds, respecting bpm changes in between
		public QNT_Timestamp ShiftTick(QNT_Timestamp startTime, float duration) {
			int currentBpmIdx = GetCurrentBPMIndex(startTime);
			if(currentBpmIdx == -1) {
				return startTime;
			}

			QNT_Timestamp currentTime = startTime;

			while(duration != 0 && currentBpmIdx >= 0 && currentBpmIdx < tempoChanges.Count) {
				var tempo = tempoChanges[currentBpmIdx];

				Relative_QNT remainingTime = Conversion.ToQNT(duration, tempo.microsecondsPerQuarterNote);
				QNT_Timestamp timeOfNextBPM = new QNT_Timestamp(0);
				int sign = Math.Sign(remainingTime.tick);

				currentBpmIdx += sign;
				if(currentBpmIdx > 0 && currentBpmIdx < tempoChanges.Count) {
					timeOfNextBPM = tempoChanges[currentBpmIdx].time;
				}

				//If there is time to another bpm we need to shift to the next bpm point, then continue
				if(timeOfNextBPM.tick != 0 && timeOfNextBPM < (currentTime + remainingTime)) {
					Relative_QNT timeUntilTempoShift = timeOfNextBPM - currentTime;
					currentTime += timeUntilTempoShift;
					duration -= Conversion.FromQNT(timeUntilTempoShift, tempo.microsecondsPerQuarterNote);
				}
				//No bpm change, apply the time and break
				else {
					currentTime += remainingTime;
					break;
				}
			}

			return currentTime;
		}

		public float TimestampToSeconds(QNT_Timestamp timestamp) {
			float duration = 0.0f;

			for(int i = 0; i < tempoChanges.Count; ++i) {
				var c = tempoChanges[i];
				
				if(timestamp >= c.time && (i + 1 >= tempoChanges.Count || timestamp < tempoChanges[i + 1].time)) {
					duration += Conversion.FromQNT(timestamp - c.time, c.microsecondsPerQuarterNote);
					break;
				}
				else if(i + 1 < tempoChanges.Count) {
					duration += Conversion.FromQNT(tempoChanges[i + 1].time - c.time, c.microsecondsPerQuarterNote);
				}
			}

			return duration;
		}

		string prevTimeText;
		private void SetCurrentTime() {
			float timeSeconds = TimestampToSeconds(time);

			string minutes = Mathf.Floor((int) timeSeconds / 60).ToString("00");
			string seconds = ((int) timeSeconds % 60).ToString("00");
			if (seconds != prevTimeText) {
				prevTimeText = seconds;
				songTimestamp.text = minutes + ":" + seconds;
			}

		}

		private string prevTickText;

		private void SetCurrentTick() {
			string currentTick = time.tick.ToString();
			if (currentTick != prevTickText) {
				prevTickText = currentTick;
				curTick.text = currentTick;
			}
		}

		private void SetAudioDSP() {
			//Pull DSP setting from config
			var configuration = AudioSettings.GetConfiguration();
			configuration.dspBufferSize = NRSettings.config.audioDSP;
			AudioSettings.Reset(configuration);
		}
	}
}