using System;
using System.Collections.Generic;
using TimboJimbo;
using TimboJimbo.Core;
using TimboJimbo.PropertyBindings;
using TimboJimboEditor.PropertyBindings.Utility;
using UnityEditor;
using UnityEngine;

namespace TimboJimboEditor
{
    internal static class TweenSequenceRecordingSession
    {
        private const float EntryTimeEpsilon = 0.001f;
        private const int MaxRecentEvents = 10;
        private const int MaxEventLength = 84;

        private static readonly List<BindableProperty> RecordingProperties = new();
        private static readonly HashSet<BindableProperty> RecordingPropertySet = new(BindablePropertyEqualityComparer.Instance);
        private static readonly List<string> RecentEvents = new();

        private static UserEditTracker _tracker;

        public static bool IsRecording { get; private set; }
        public static TweenSequence Target { get; private set; }
        public static float PlayheadTime { get; private set; }

        public static event Action Changed;

        [InitializeOnLoadMethod]
        private static void Init()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        public static void Start(TweenSequence target, float playheadTime)
        {
            if (target == null)
                return;

            if (IsRecording && Target == target)
            {
                PlayheadTime = Mathf.Max(0f, playheadTime);
                NotifyChanged();
                return;
            }

            if (IsRecording)
                Stop();

            Target = target;
            PlayheadTime = Mathf.Max(0f, playheadTime);

            RecordingProperties.Clear();
            RecordingPropertySet.Clear();
            RecentEvents.Clear();

            BindablePropertyUtility.GetBindableProperties(target.gameObject, RecordingProperties, recursive: true);
            for (int i = 0; i < RecordingProperties.Count; i++)
                RecordingPropertySet.Add(RecordingProperties[i]);

            _tracker?.StopDetecting();
            _tracker = new UserEditTracker(filterOut: property => !RecordingPropertySet.Contains(property));
            _tracker.StartDetecting(OnRecordingEdit);

            IsRecording = true;
            AddEvent($"Recording started for '{target.name}'.");
            NotifyChanged();
        }

        public static void Stop()
        {
            if (!IsRecording && _tracker == null)
                return;

            _tracker?.StopDetecting();
            _tracker = null;

            RecordingProperties.Clear();
            RecordingPropertySet.Clear();

            if (Target != null)
                AddEvent($"Recording stopped for '{Target.name}'.");

            IsRecording = false;
            Target = null;
            PlayheadTime = 0f;

            NotifyChanged();
        }

        public static void SetPlayhead(float playheadTime)
        {
            PlayheadTime = Mathf.Max(0f, playheadTime);
            NotifyChanged();
        }

        public static void CopyRecentEvents(List<string> destination)
        {
            destination.Clear();
            for (int i = 0; i < RecentEvents.Count; i++)
                destination.Add(RecentEvents[i]);
        }

        private static void OnRecordingEdit(EditType editType, BindablePropertyValueEdit edit)
        {
            if (!IsRecording || Target == null)
                return;

            if (editType == EditType.Removed)
                return;

            RecordEdit(edit);
        }

        private static void RecordEdit(BindablePropertyValueEdit edit)
        {
            if (Target == null)
                return;

            Undo.RecordObject(Target, "Record Tween Edit");

            var track = FindOrCreateTrack(edit.BindableProperty);
            if (track == null)
                return;

            var entry = FindEntryAtTime(track, PlayheadTime);
            if (entry == null)
            {
                entry = new AnimatedValueTrackEntry
                {
                    StartTime = PlayheadTime,
                    Duration = 1f,
                    Animator = new EasedAnimator
                    {
                        Ease = EaseType.InOutQuad,
                        StartMode = EasedStartMode.StartFromAbsolute,
                        EndMode = EasedEndMode.EndAtAbsolute,
                        StartValue = edit.InitialValue,
                        EndValue = edit.LatestValue,
                    }
                };
                track.AddEntry(entry);
                AddEvent($"Created entry: {GetPropertyLabel(edit.BindableProperty)} @ {PlayheadTime:0.###}s");
            }
            else
            {
                if (entry.Animator is not EasedAnimator eased)
                {
                    eased = new EasedAnimator
                    {
                        Ease = EaseType.InOutQuad,
                        StartMode = EasedStartMode.StartFromAbsolute,
                        EndMode = EasedEndMode.EndAtAbsolute,
                        StartValue = edit.InitialValue,
                        EndValue = edit.LatestValue,
                    };
                    entry.Animator = eased;
                }
                else
                {
                    eased.EndMode = EasedEndMode.EndAtAbsolute;
                    eased.EndValue = edit.LatestValue;
                }

                AddEvent($"Updated entry: {GetPropertyLabel(edit.BindableProperty)} @ {PlayheadTime:0.###}s");
            }

            Target.MarkBidingsDirty();
            Target.MarkTimingDirty();
            EditorUtility.SetDirty(Target);

            NotifyChanged();
        }

        private static AnimatedValueTrack FindOrCreateTrack(in BindableProperty property)
        {
            if (Target == null)
                return null;

            for (int i = 0; i < Target.Tracks.Count; i++)
            {
                var existing = Target.Tracks[i];
                if (existing != null && existing.Property.Equals(property))
                    return existing;
            }

            var created = new AnimatedValueTrack
            {
                Property = property
            };

            Target.AddTrack(created);
            return created;
        }

        private static AnimatedValueTrackEntry FindEntryAtTime(AnimatedValueTrack track, float time)
        {
            if (track == null)
                return null;

            for (int i = 0; i < track.Entries.Count; i++)
            {
                var entry = track.Entries[i];
                if (entry == null)
                    continue;

                if (Mathf.Abs(entry.StartTime - time) <= EntryTimeEpsilon)
                    return entry;
            }

            return null;
        }

        private static string GetPropertyLabel(in BindableProperty property)
        {
            if (property.Target is Component component)
            {
                string goName = component.gameObject != null ? component.gameObject.name : "(Missing GO)";
                string componentName = ObjectNames.NicifyVariableName(component.GetType().Name);
                string propertyName = ObjectNames.NicifyVariableName(property.Path);
                return $"{goName} → {componentName} → {propertyName}";
            }

            if (property.Target is GameObject gameObject)
                return $"{gameObject.name} → {ObjectNames.NicifyVariableName(property.Path)}";

            return ObjectNames.NicifyVariableName(property.Path);
        }

        private static void AddEvent(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string text = message.Length <= MaxEventLength
                ? message
                : message.Substring(0, MaxEventLength - 1) + "…";

            RecentEvents.Insert(0, text);
            if (RecentEvents.Count > MaxRecentEvents)
                RecentEvents.RemoveRange(MaxRecentEvents, RecentEvents.Count - MaxRecentEvents);
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state is PlayModeStateChange.ExitingEditMode or PlayModeStateChange.ExitingPlayMode)
                Stop();
        }

        private static void OnBeforeAssemblyReload()
        {
            Stop();
        }

        private static void NotifyChanged()
        {
            SceneView.RepaintAll();
            foreach (var editor in ActiveEditorTracker.sharedTracker.activeEditors)
                editor.Repaint();
            Changed?.Invoke();
        }
    }
}
