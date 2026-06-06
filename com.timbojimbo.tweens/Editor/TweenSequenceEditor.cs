using System;
using System.Collections.Generic;
using TimboJimbo;
using TimboJimbo.Core;
using TimboJimbo.PropertyBindings;
using TimboJimboEditor.Core;
using TimboJimboEditor.PropertyBindings.Utility;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TimboJimboEditor
{
    [CustomEditor(typeof(TweenSequence))]
    public partial class TweenSequenceEditor : Editor
    {
        private SerializedProperty _tracksProp;
        private SerializedProperty _playOnEnableProp;
        private SerializedProperty _loopProp;

        private bool _previewing;
        private bool _playing;
        private float _playheadTime;
        private double _lastEditorTime;

        private PropertyBindingCollection _previewBindings;
        private readonly Dictionary<BindableProperty, ValueContainer> _initialValues = new(BindablePropertyEqualityComparer.Instance);

        private readonly GUIContent _playLabel = new("Play");
        private readonly GUIContent _pauseLabel = new("Pause");
        private readonly GUIContent _stopLabel = new("Stop");

        private static readonly int InspectorDividerControlId = "TweenSequenceEditorInspectorDivider".GetHashCode();

        private const float MinTimelineDuration = 5f;
        private const float TimeRulerHeight = 22f;
        private const float TrackLabelWidth = 180f;
        private const float TrackRowHeight = 36f;
        private const float MinTimelineHeight = 200f;
        private const float SnapThresholdPx = 6f;
        private static readonly int TimelineControlId = "TweenSequenceEditorTimeline".GetHashCode();

        private enum TimelineDragKind { None, Playhead, EntryMove, EntryResizeStart, EntryResizeEnd }
        private TimelineDragKind _dragKind;
        private int _dragTrackIndex = -1;
        private int _dragEntryIndex = -1;
        private float _dragGrabOffset;
        private float _dragOriginalStart;
        private float _dragOriginalDuration;
        private float _dragGhostStart;
        private float _dragGhostEnd;
        private Rect _dragRowRect;

        private int _selectedTrackIndex = -1;
        private int _selectedEntryIndex = -1;

        private readonly List<(float time, float yMin, float yMax)> _snapLines = new();
        private readonly HashSet<float> _snapPoints = new();
        private readonly List<EntryHit> _entryHits = new();

        private struct EntryHit
        {
            public int TrackIndex;
            public int EntryIndex;
            public Rect Bar;
            public Rect RowRect;
            public Rect LeftEdge;
            public Rect RightEdge;
        }

        private float _inspectorWidth = 300f;
        private bool _isResizingInspector;
        private float _resizeStartMouseX;
        private float _resizeStartInspectorWidth;

        private void OnEnable()
        {
            _tracksProp = serializedObject.FindProperty("_tracks");
            _playOnEnableProp = serializedObject.FindProperty("_playOnEnable");
            _loopProp = serializedObject.FindProperty("_loop");
        }

        private void OnDisable()
        {
            ExitPreview();
        }

        public override bool RequiresConstantRepaint() => _previewing && _playing;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_playOnEnableProp);
            EditorGUILayout.PropertyField(_loopProp);

            EditorGUILayout.Space();
            DrawTrackToolbar();

            EditorGUILayout.Space();
            DrawPreviewControls();

            EditorGUILayout.Space();
            DrawRecordingControls();

            EditorGUILayout.Space();
            DrawTimelineAndInspector();

            serializedObject.ApplyModifiedProperties();

            if (_previewing && _playing)
                TickEditorPlayback();
        }

        private void DrawTrackToolbar()
        {
            EditorGUILayout.LabelField("Tracks", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Track", GUILayout.Height(22)))
                    AddTrack();

                using (new EditorGUI.DisabledScope(_selectedTrackIndex < 0 || _selectedTrackIndex >= _tracksProp.arraySize))
                {
                    if (GUILayout.Button("Remove Selected Track", GUILayout.Height(22)))
                        RemoveSelectedTrack();
                }
            }
        }

        private void AddTrack()
        {
            int index = _tracksProp.arraySize;
            _tracksProp.InsertArrayElementAtIndex(index);

            var trackProp = _tracksProp.GetArrayElementAtIndex(index);
            var propertyProp = trackProp.FindPropertyRelative(nameof(AnimatedValueTrack.Property));
            propertyProp.FindPropertyRelative("_target").objectReferenceValue = null;
            propertyProp.FindPropertyRelative("_path").stringValue = string.Empty;
            propertyProp.FindPropertyRelative("_kind").enumValueIndex = (int)ValueKind.Float;
            propertyProp.FindPropertyRelative("_componentLayout").enumValueIndex = (int)ComponentLayout.One;
            propertyProp.FindPropertyRelative("_componentOnePath").stringValue = string.Empty;
            propertyProp.FindPropertyRelative("_componentTwoPath").stringValue = string.Empty;
            propertyProp.FindPropertyRelative("_componentThreePath").stringValue = string.Empty;
            propertyProp.FindPropertyRelative("_componentFourPath").stringValue = string.Empty;

            trackProp.FindPropertyRelative("_entries").ClearArray();

            serializedObject.ApplyModifiedProperties();
            ((TweenSequence)target).MarkBidingsDirty();
            ((TweenSequence)target).MarkTimingDirty();

            _selectedTrackIndex = index;
            _selectedEntryIndex = -1;
            Repaint();
        }

        private void RemoveSelectedTrack()
        {
            if (_selectedTrackIndex < 0 || _selectedTrackIndex >= _tracksProp.arraySize)
                return;

            _tracksProp.DeleteArrayElementAtIndex(_selectedTrackIndex);
            serializedObject.ApplyModifiedProperties();

            ((TweenSequence)target).MarkBidingsDirty();
            ((TweenSequence)target).MarkTimingDirty();

            _selectedTrackIndex = -1;
            _selectedEntryIndex = -1;

            if (_previewing)
                SamplePreview();
        }

        private void DrawPreviewControls()
        {
            var seq = (TweenSequence)target;
            float duration = seq.Duration;

            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_playing))
                {
                    if (GUILayout.Button(_playLabel, GUILayout.Height(24)))
                    {
                        EnterPreview();
                        _playing = true;
                        _lastEditorTime = EditorApplication.timeSinceStartup;
                    }
                }

                using (new EditorGUI.DisabledScope(!_playing))
                {
                    if (GUILayout.Button(_pauseLabel, GUILayout.Height(24)))
                        _playing = false;
                }

                using (new EditorGUI.DisabledScope(!_previewing))
                {
                    if (GUILayout.Button(_stopLabel, GUILayout.Height(24)))
                        ExitPreview();
                }
            }

            EditorGUI.BeginChangeCheck();
            float newTime = EditorGUILayout.Slider("Time", _playheadTime, 0f, Mathf.Max(0.0001f, Mathf.Max(duration, MinTimelineDuration)));
            if (EditorGUI.EndChangeCheck())
                SetPlayhead(newTime, true);

            EditorGUILayout.LabelField($"Duration: {duration:0.###}s", EditorStyles.miniLabel);
        }

        private void DrawRecordingControls()
        {
            EditorGUILayout.LabelField("Recording", EditorStyles.boldLabel);
            bool isRecordingThisTarget = IsRecordingThisTarget();

            using (new EditorGUILayout.HorizontalScope())
            {
                var previousColor = GUI.backgroundColor;
                if (isRecordingThisTarget)
                    GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);

                if (GUILayout.Button(isRecordingThisTarget ? "Stop Recording" : "Start Recording", GUILayout.Height(24)))
                {
                    if (isRecordingThisTarget)
                        StopRecording();
                    else
                        StartRecording();
                }

                GUI.backgroundColor = previousColor;
            }

            EditorGUILayout.HelpBox(
                isRecordingThisTarget
                    ? "Recording is on. Edited properties immediately create or update entries at the current playhead time."
                    : "Press Start Recording, then tweak properties to capture entries at the current playhead time.",
                MessageType.Info);
        }

        private void StartRecording()
        {
            TweenSequenceRecordingSession.Start((TweenSequence)target, _playheadTime);
            RepaintAllEditors();
        }

        private void StopRecording()
        {
            if (!IsRecordingThisTarget())
                return;

            TweenSequenceRecordingSession.Stop();
            RepaintAllEditors();
        }

        private bool IsRecordingThisTarget()
        {
            return TweenSequenceRecordingSession.IsRecording && TweenSequenceRecordingSession.Target == (TweenSequence)target;
        }

        private static void RepaintAllEditors()
        {
            SceneView.RepaintAll();
            foreach (var editor in ActiveEditorTracker.sharedTracker.activeEditors)
                editor.Repaint();
        }

        private void EnterPreview()
        {
            if (_previewing)
                return;

            _previewing = true;

            var seq = (TweenSequence)target;
            CaptureInitialValues(seq);
            seq.CaptureBaselines();
            SamplePreview();
        }

        private void ExitPreview()
        {
            if (!_previewing)
                return;

            RestoreInitialValues();

            var seq = (TweenSequence)target;
            seq.ClearBaselines();

            _previewing = false;
            _playing = false;
            _playheadTime = 0f;
        }

        private void SetPlayhead(float time, bool fromUserScrub)
        {
            if (fromUserScrub)
                _playing = false;

            _playheadTime = Mathf.Max(0f, time);
            if (IsRecordingThisTarget())
                TweenSequenceRecordingSession.SetPlayhead(_playheadTime);

            if (_previewing)
                SamplePreview();
        }

        private void TickEditorPlayback()
        {
            var seq = (TweenSequence)target;
            var now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - _lastEditorTime);
            _lastEditorTime = now;

            _playheadTime += dt;
            float duration = seq.Duration;

            if (seq.Loop && duration > 0f)
            {
                while (_playheadTime >= duration)
                    _playheadTime -= duration;
            }
            else if (_playheadTime >= duration)
            {
                _playheadTime = duration;
                _playing = false;
            }

            SamplePreview();
            Repaint();
        }

        private void SamplePreview()
        {
            var seq = (TweenSequence)target;
            seq.Sample(_playheadTime);
            MarkAffectedTargetsDirty();
        }

        private void CaptureInitialValues(TweenSequence seq)
        {
            _initialValues.Clear();

            var properties = new List<BindableProperty>();
            var seen = new HashSet<BindableProperty>(BindablePropertyEqualityComparer.Instance);
            for (int i = 0; i < seq.Tracks.Count; i++)
            {
                var track = seq.Tracks[i];
                if (!TryGetValidProperty(track, out var property))
                    continue;

                if (seen.Add(property))
                    properties.Add(property);
            }

            _previewBindings?.Dispose();
            _previewBindings = PropertyBindingCollection.Bind(seq.gameObject, properties);

            for (int i = 0; i < properties.Count; i++)
            {
                var property = properties[i];
                if (_previewBindings.TryRead(property, out var value))
                    _initialValues[property] = value;
            }
        }

        private void RestoreInitialValues()
        {
            if (_previewBindings == null)
                return;

            using (var bulk = _previewBindings.StartBulkWriteScope())
            {
                foreach (var pair in _initialValues)
                    bulk.TryWrite(pair.Key, pair.Value);
            }

            _initialValues.Clear();
            _previewBindings.Dispose();
            _previewBindings = null;
            SceneView.RepaintAll();
        }

        private void MarkAffectedTargetsDirty()
        {
            for (int i = 0; i < _tracksProp.arraySize; i++)
            {
                var target = GetTrackPropertyTarget(_tracksProp.GetArrayElementAtIndex(i));
                if (target != null)
                    EditorUtility.SetDirty(target);
            }

            SceneView.RepaintAll();
        }

        private void DrawTimelineAndInspector()
        {
            const float dividerWidth = 4f;
            const float minInspectorWidth = 220f;
            const float minTimelineWidth = 260f;

            float timelineHeight = CalculateTimelineHeight();
            float panelHeight = Mathf.Max(timelineHeight, 360f);
            var outerRect = GUILayoutUtility.GetRect(0f, panelHeight, GUILayout.ExpandWidth(true));

            float available = Mathf.Max(outerRect.width, EditorGUIUtility.currentViewWidth);
            float maxInspectorWidth = Mathf.Max(minInspectorWidth, available - minTimelineWidth - dividerWidth);
            _inspectorWidth = Mathf.Clamp(_inspectorWidth, minInspectorWidth, maxInspectorWidth);

            var dividerHitRect = new Rect(outerRect.x + _inspectorWidth - 4f, outerRect.y, dividerWidth + 8f, outerRect.height);
            HandleDividerResize(dividerHitRect, minInspectorWidth, maxInspectorWidth);
            _inspectorWidth = Mathf.Clamp(_inspectorWidth, minInspectorWidth, maxInspectorWidth);

            var inspectorRect = new Rect(outerRect.x, outerRect.y, _inspectorWidth, outerRect.height);
            var dividerRect = new Rect(inspectorRect.xMax, outerRect.y, dividerWidth, outerRect.height);
            var timelineRect = new Rect(dividerRect.xMax, outerRect.y, Mathf.Max(0f, outerRect.xMax - dividerRect.xMax), outerRect.height);

            GUI.BeginGroup(inspectorRect);
            GUILayout.BeginArea(new Rect(0f, 0f, inspectorRect.width, inspectorRect.height));
            DrawSelectedEntryInspector();
            GUILayout.EndArea();
            GUI.EndGroup();

            EditorGUI.DrawRect(new Rect(dividerRect.x + 1.5f, dividerRect.y, 1f, dividerRect.height), new Color(0.4f, 0.4f, 0.4f));
            DrawTimelineInRect(timelineRect);
        }

        private float CalculateTimelineHeight()
        {
            int trackCount = Mathf.Max(1, _tracksProp.arraySize);
            return TimeRulerHeight + Mathf.Max(MinTimelineHeight, trackCount * TrackRowHeight) + 6f;
        }

        private void HandleDividerResize(Rect dividerHitRect, float minInspectorWidth, float maxInspectorWidth)
        {
            int id = GUIUtility.GetControlID(InspectorDividerControlId, FocusType.Passive, dividerHitRect);
            var e = Event.current;
            EditorGUIUtility.AddCursorRect(dividerHitRect, MouseCursor.ResizeHorizontal, id);

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && dividerHitRect.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        _isResizingInspector = true;
                        _resizeStartMouseX = e.mousePosition.x;
                        _resizeStartInspectorWidth = _inspectorWidth;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id && _isResizingInspector)
                    {
                        float delta = e.mousePosition.x - _resizeStartMouseX;
                        _inspectorWidth = Mathf.Clamp(_resizeStartInspectorWidth + delta, minInspectorWidth, maxInspectorWidth);
                        Repaint();
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        _isResizingInspector = false;
                        e.Use();
                    }
                    break;
            }
        }

        private void DrawTimelineInRect(Rect outer)
        {
            var seq = (TweenSequence)target;
            int trackCount = Mathf.Max(1, _tracksProp.arraySize);
            bool empty = _tracksProp.arraySize == 0;

            var ruler = new Rect(outer.x + TrackLabelWidth, outer.y, outer.width - TrackLabelWidth, TimeRulerHeight);
            var content = new Rect(ruler.x, ruler.yMax, ruler.width, outer.height - TimeRulerHeight - 6f);
            var labels = new Rect(outer.x, content.y, TrackLabelWidth, content.height);

            float duration = Mathf.Max(seq.Duration, MinTimelineDuration);

            EditorGUI.DrawRect(outer, new Color(0.16f, 0.16f, 0.16f));
            EditorGUI.DrawRect(ruler, new Color(0.22f, 0.22f, 0.22f));
            EditorGUI.DrawRect(content, new Color(0.18f, 0.18f, 0.18f));
            EditorGUI.DrawRect(labels, new Color(0.22f, 0.22f, 0.22f));

            DrawRulerTicks(ruler, content, duration);

            _entryHits.Clear();
            _snapLines.Clear();
            if (_dragTrackIndex >= 0 && _dragEntryIndex >= 0)
                CollectSnapPoints(duration, content);

            if (empty)
            {
                GUI.Label(new Rect(labels.x + 4f, content.y + 8f, labels.width - 8f, 18f), "(no tracks)", EditorStyles.miniLabel);
            }

            for (int ti = 0; ti < trackCount; ti++)
            {
                var rowRect = new Rect(content.x, content.y + ti * TrackRowHeight, content.width, TrackRowHeight);
                var labelRect = new Rect(labels.x + 4f, rowRect.y + 2f, labels.width - 8f, rowRect.height - 4f);

                if ((ti & 1) == 1)
                    EditorGUI.DrawRect(rowRect, new Color(1f, 1f, 1f, 0.025f));

                EditorGUI.DrawRect(new Rect(content.x, rowRect.y, content.width, 1f), new Color(1f, 1f, 1f, 0.08f));

                if (empty)
                    continue;

                var trackProp = _tracksProp.GetArrayElementAtIndex(ti);
                GUI.Label(labelRect, new GUIContent(GetTrackDisplayName(trackProp)), EditorStyles.miniLabel);

                var entriesProp = trackProp.FindPropertyRelative("_entries");
                for (int ei = 0; ei < entriesProp.arraySize; ei++)
                {
                    var entryProp = entriesProp.GetArrayElementAtIndex(ei);
                    DrawEntryBar(ti, ei, entryProp, rowRect, duration, _entryHits);
                }
            }

            foreach (var (snapTime, yMin, yMax) in _snapLines)
            {
                float sx = content.x + (snapTime / duration) * content.width;
                sx = Mathf.Clamp(sx, content.x, content.xMax);
                var snapLine = new Rect(sx - 0.5f, yMin, 1f, yMax - yMin);
                EditorGUI.DrawRect(snapLine, new Color(1f, 1f, 0.3f, 0.7f));
            }

            HandleTimelineInteraction(ruler, content, duration);
            DrawPlayhead(content, ruler, duration);
        }

        private void DrawEntryBar(int trackIdx, int entryIdx, SerializedProperty entryProp, Rect rowRect, float timelineDuration, List<EntryHit> hits)
        {
            var startTimeProp = entryProp.FindPropertyRelative(nameof(AnimatedValueTrackEntry.StartTime));
            var durProp = entryProp.FindPropertyRelative(nameof(AnimatedValueTrackEntry.Duration));
            float startTime = startTimeProp.floatValue;
            float duration = durProp.floatValue;
            float endTime = startTime + duration;

            float x0 = rowRect.x + (startTime / timelineDuration) * rowRect.width;
            float x1 = rowRect.x + (endTime / timelineDuration) * rowRect.width;
            float visualWidth = Mathf.Max(6f, x1 - x0);
            var bar = new Rect(x0, rowRect.y + 4f, visualWidth, Mathf.Max(6f, rowRect.height - 8f));

            bool isDragging = _dragTrackIndex == trackIdx && _dragEntryIndex == entryIdx && _dragKind != TimelineDragKind.None;
            bool isSelected = _selectedTrackIndex == trackIdx && _selectedEntryIndex == entryIdx;

            // Ghost: show original/natural extent when clip is truncated during drag
            if (isDragging && duration < _dragOriginalDuration - 0.0001f)
            {
                float gx0 = rowRect.x + (_dragGhostStart / timelineDuration) * rowRect.width;
                float gx1 = rowRect.x + (_dragGhostEnd / timelineDuration) * rowRect.width;
                var ghostBar = new Rect(gx0, bar.y, Mathf.Max(1f, gx1 - gx0), bar.height);
                EditorGUI.DrawRect(ghostBar, new Color(0.45f, 0.65f, 1f, 0.12f));
                DrawOutline(ghostBar, new Color(0.45f, 0.65f, 1f, 0.25f));
            }

            EditorGUI.DrawRect(bar, GetEntryColor(isSelected, isDragging));

            if (isSelected)
                DrawOutline(bar, new Color(1f, 1f, 1f, 0.6f));

            var animName = GetAnimatorShortName(entryProp.FindPropertyRelative(nameof(AnimatedValueTrackEntry.Animator)));
            var labelRect = new Rect(bar.x + 4f, bar.y + 2f, bar.width - 8f, bar.height - 4f);
            GUI.Label(labelRect, $"{animName} {startTime:0.##}s→{endTime:0.##}s", EditorStyles.miniLabel);

            const float edgeGrab = 5f;
            var leftEdge = new Rect(bar.x, bar.y, edgeGrab, bar.height);
            var rightEdge = new Rect(bar.xMax - edgeGrab, bar.y, edgeGrab, bar.height);

            EditorGUIUtility.AddCursorRect(bar, MouseCursor.MoveArrow);
            EditorGUIUtility.AddCursorRect(leftEdge, MouseCursor.ResizeHorizontal);
            EditorGUIUtility.AddCursorRect(rightEdge, MouseCursor.ResizeHorizontal);

            hits.Add(new EntryHit
            {
                TrackIndex = trackIdx,
                EntryIndex = entryIdx,
                Bar = bar,
                RowRect = rowRect,
                LeftEdge = leftEdge,
                RightEdge = rightEdge,
            });
        }

        private void BeginEntryDrag(int id, int trackIdx, int entryIdx, Vector2 mousePosition, Rect rowRect, float timelineDuration,
            SerializedProperty startTimeProp, SerializedProperty durProp, Rect leftEdge, Rect rightEdge)
        {
            SelectEntry(trackIdx, entryIdx);

            GUIUtility.hotControl = id;
            _dragKind = rightEdge.Contains(mousePosition)
                ? TimelineDragKind.EntryResizeEnd
                : leftEdge.Contains(mousePosition)
                    ? TimelineDragKind.EntryResizeStart
                    : TimelineDragKind.EntryMove;

            _dragTrackIndex = trackIdx;
            _dragEntryIndex = entryIdx;
            _dragOriginalStart = startTimeProp.floatValue;
            _dragOriginalDuration = durProp.floatValue;
            _dragGhostStart = _dragOriginalStart;
            _dragGhostEnd = _dragOriginalStart + _dragOriginalDuration;
            _dragGrabOffset = MouseToTime(mousePosition.x, rowRect, timelineDuration) - startTimeProp.floatValue;
        }

        private void EndTimelineDrag()
        {
            if (GUIUtility.hotControl != 0)
                GUIUtility.hotControl = 0;

            _dragKind = TimelineDragKind.None;
            _dragTrackIndex = -1;
            _dragEntryIndex = -1;
            _dragRowRect = default;
        }

        private void GetNeighborBounds(int trackIdx, int entryIdx, float queryStart, float queryEnd, out float leftBound, out float rightBound)
        {
            leftBound = 0f;
            rightBound = float.MaxValue;

            var entriesProp = GetEntriesProp(trackIdx);
            if (entriesProp == null) return;

            for (int i = 0; i < entriesProp.arraySize; i++)
            {
                if (i == entryIdx) continue;
                var ep = entriesProp.GetArrayElementAtIndex(i);
                float s = ep.FindPropertyRelative(nameof(AnimatedValueTrackEntry.StartTime)).floatValue;
                float d = ep.FindPropertyRelative(nameof(AnimatedValueTrackEntry.Duration)).floatValue;
                float end = s + d;

                // Clip is predominantly to the left (ends at or before our natural start)
                if (end <= queryStart + 0.0001f)
                    leftBound = Mathf.Max(leftBound, end);
                // Clip is predominantly to the right (starts at or after our natural end)
                else if (s >= queryEnd - 0.0001f)
                    rightBound = Mathf.Min(rightBound, s);
                // Clip overlaps from the left side
                else if (s < queryStart)
                    leftBound = Mathf.Max(leftBound, end);
                // Clip overlaps from the right side
                else
                    rightBound = Mathf.Min(rightBound, s);
            }
        }

        private void DeleteZeroDurationAndEmptyTracks()
        {
            bool changed = false;

            for (int ti = 0; ti < _tracksProp.arraySize; ti++)
            {
                var entriesProp = GetEntriesProp(ti);
                if (entriesProp == null) continue;

                for (int ei = entriesProp.arraySize - 1; ei >= 0; ei--)
                {
                    var ep = entriesProp.GetArrayElementAtIndex(ei);
                    float dur = ep.FindPropertyRelative(nameof(AnimatedValueTrackEntry.Duration)).floatValue;
                    if (dur <= 0f)
                    {
                        entriesProp.DeleteArrayElementAtIndex(ei);
                        changed = true;
                        AdjustSelectionAfterEntryDeletion(ti, ei);
                    }
                }
            }

            for (int ti = _tracksProp.arraySize - 1; ti >= 0; ti--)
            {
                var entriesProp = GetEntriesProp(ti);
                if (entriesProp != null && entriesProp.arraySize == 0)
                {
                    _tracksProp.DeleteArrayElementAtIndex(ti);
                    changed = true;
                    AdjustSelectionAfterTrackDeletion(ti);
                }
            }

            if (changed)
            {
                serializedObject.ApplyModifiedProperties();
                ((TweenSequence)target).MarkBidingsDirty();
                ((TweenSequence)target).MarkTimingDirty();
                if (_previewing) SamplePreview();
            }
        }

        private void ApplyTimingChanges()
        {
            serializedObject.ApplyModifiedProperties();
            ((TweenSequence)target).MarkTimingDirty();
            if (_previewing) SamplePreview();
            Repaint();
        }

        private void ShowEntryContextMenu(int trackIdx, int entryIdx)
        {
            var menu = new GenericMenu();
            int ct = trackIdx;
            int ce = entryIdx;
            menu.AddItem(new GUIContent("Move Start To Playhead"), false, () =>
            {
                var ep = GetEntriesProp(ct)?.GetArrayElementAtIndex(ce);
                if (ep == null) return;
                ep.FindPropertyRelative(nameof(AnimatedValueTrackEntry.StartTime)).floatValue = _playheadTime;
                ApplyTimingChanges();
            });
            menu.AddItem(new GUIContent("Delete"), false, () =>
            {
                var ep = GetEntriesProp(ct);
                if (ep == null) return;
                ep.DeleteArrayElementAtIndex(ce);
                serializedObject.ApplyModifiedProperties();
                AdjustSelectionAfterEntryDeletion(ct, ce);
                ((TweenSequence)target).MarkTimingDirty();
                if (_previewing) SamplePreview();
            });
            menu.ShowAsContext();
        }

        private void DrawSelectedEntryInspector()
        {
            EditorGUILayout.LabelField("Entry Inspector", EditorStyles.boldLabel);

            if (_selectedTrackIndex < 0 || _selectedTrackIndex >= _tracksProp.arraySize)
            {
                EditorGUILayout.LabelField("(select a track/entry on the timeline)", EditorStyles.miniLabel);
                return;
            }

            var trackProp = _tracksProp.GetArrayElementAtIndex(_selectedTrackIndex);
            var propertyProp = trackProp.FindPropertyRelative(nameof(AnimatedValueTrack.Property));
            DrawBindablePropertyPicker(propertyProp);

            var entriesProp = GetEntriesProp(_selectedTrackIndex);
            if (entriesProp == null)
                return;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Entry", GUILayout.Height(20)))
                {
                    int created = CreateEntryAt(entriesProp, _playheadTime);
                    SelectEntry(_selectedTrackIndex, created);
                    serializedObject.ApplyModifiedProperties();
                    ((TweenSequence)target).MarkTimingDirty();
                    if (_previewing) SamplePreview();
                    return;
                }

                using (new EditorGUI.DisabledScope(_selectedEntryIndex < 0 || _selectedEntryIndex >= entriesProp.arraySize))
                {
                    if (GUILayout.Button("Delete Entry", GUILayout.Height(20)))
                    {
                        entriesProp.DeleteArrayElementAtIndex(_selectedEntryIndex);
                        AdjustSelectionAfterEntryDeletion(_selectedTrackIndex, _selectedEntryIndex);
                        serializedObject.ApplyModifiedProperties();
                        ((TweenSequence)target).MarkTimingDirty();
                        if (_previewing) SamplePreview();
                        return;
                    }
                }
            }

            if (_selectedEntryIndex < 0 || _selectedEntryIndex >= entriesProp.arraySize)
            {
                EditorGUILayout.LabelField("(select an entry)", EditorStyles.miniLabel);
                return;
            }

            var entryProp = entriesProp.GetArrayElementAtIndex(_selectedEntryIndex);
            var startTimeProp = entryProp.FindPropertyRelative(nameof(AnimatedValueTrackEntry.StartTime));
            var durProp = entryProp.FindPropertyRelative(nameof(AnimatedValueTrackEntry.Duration));
            var animatorProp = entryProp.FindPropertyRelative(nameof(AnimatedValueTrackEntry.Animator));
            var animator = animatorProp.managedReferenceValue as ValueAnimator;

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.PropertyField(startTimeProp, new GUIContent("Start"));
            EditorGUILayout.PropertyField(durProp, new GUIContent("Duration"));

            DrawAnimatorTypeRow(animatorProp);
            DrawAnimatorParams(animatorProp, animator);

            if (EditorGUI.EndChangeCheck())
                ApplyTimingChanges();
        }

        private void DrawAnimatorTypeRow(SerializedProperty animatorProp)
        {
            var anim = animatorProp.managedReferenceValue as ValueAnimator;
            var rect = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
            var labelRect = new Rect(rect.x, rect.y, EditorGUIUtility.labelWidth, rect.height);
            var buttonRect = new Rect(labelRect.xMax, rect.y, rect.width - labelRect.width, rect.height);

            EditorGUI.LabelField(labelRect, "Animator");
            var typeName = anim != null ? ObjectNames.NicifyVariableName(anim.GetType().Name) : "(none)";
            if (EditorGUI.DropdownButton(buttonRect, new GUIContent(typeName), FocusType.Passive))
                ShowAnimatorTypeMenu(animatorProp);
        }

        private void ShowAnimatorTypeMenu(SerializedProperty animatorProp)
        {
            var menu = new GenericMenu();
            var cp = animatorProp;

            menu.AddItem(new GUIContent("Eased"), animatorProp.managedReferenceValue is EasedAnimator,
                () => SetAnimatorType(cp, new EasedAnimator { Ease = EaseType.InOutQuad }));
            menu.AddItem(new GUIContent("Shake"), animatorProp.managedReferenceValue is ShakeAnimator,
                () => SetAnimatorType(cp, new ShakeAnimator()));
            menu.AddItem(new GUIContent("Punch"), animatorProp.managedReferenceValue is PunchAnimator,
                () => SetAnimatorType(cp, new PunchAnimator()));

            menu.ShowAsContext();
        }

        private void SetAnimatorType(SerializedProperty animatorProp, ValueAnimator newAnimator)
        {
            var oldEased = animatorProp.managedReferenceValue as EasedAnimator;
            var newEased = newAnimator as EasedAnimator;
            if (oldEased != null && newEased != null)
            {
                newEased.StartMode = oldEased.StartMode;
                newEased.EndMode = oldEased.EndMode;
                newEased.StartValue = oldEased.StartValue;
                newEased.EndValue = oldEased.EndValue;
                newEased.Ease = oldEased.Ease;
            }

            animatorProp.managedReferenceValue = newAnimator;
            serializedObject.ApplyModifiedProperties();
            if (_previewing) SamplePreview();
        }

        private void DrawAnimatorParams(SerializedProperty animatorProp, ValueAnimator animator)
        {
            if (animator is EasedAnimator)
                DrawEasedParams(animatorProp);
            else if (animator != null)
                DrawDefaultAnimatorChildren(animatorProp);
        }

        private static void DrawDefaultAnimatorChildren(SerializedProperty animatorProp)
        {
            var endProp = animatorProp.GetEndProperty();
            var child = animatorProp.Copy();
            child.NextVisible(true);

            while (!SerializedProperty.EqualContents(child, endProp))
            {
                EditorGUILayout.PropertyField(child, true);
                child.NextVisible(false);
            }
        }

        private void DrawEasedParams(SerializedProperty animatorProp)
        {
            var easeProp = animatorProp.FindPropertyRelative(nameof(EasedAnimator.Ease));
            var startModeProp = animatorProp.FindPropertyRelative(nameof(EasedAnimator.StartMode));
            var endModeProp = animatorProp.FindPropertyRelative(nameof(EasedAnimator.EndMode));
            var startValueProp = animatorProp.FindPropertyRelative(nameof(EasedAnimator.StartValue));
            var endValueProp = animatorProp.FindPropertyRelative(nameof(EasedAnimator.EndValue));
            var interpolationProp = animatorProp.FindPropertyRelative(nameof(EasedAnimator.Interpolation));
            var discreteProp = animatorProp.FindPropertyRelative(nameof(EasedAnimator.DiscreteValueSelection));

            var easeRect = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
            var easeButtonRect = EditorGUI.PrefixLabel(easeRect, new GUIContent("Ease"));
            var currentEase = (EaseType)easeProp.enumValueIndex;
            CoreEditorGUI.EaseTypePopup(easeButtonRect, currentEase, ease => easeProp.enumValueIndex = (int)ease);

            EditorGUILayout.PropertyField(startModeProp, new GUIContent("From"));
            if ((EasedStartMode)startModeProp.enumValueIndex == EasedStartMode.StartFromAbsolute)
                EditorGUILayout.PropertyField(startValueProp, new GUIContent("Start Value"), true);

            EditorGUILayout.PropertyField(endModeProp, new GUIContent("To"));
            if ((EasedEndMode)endModeProp.enumValueIndex != EasedEndMode.EndAtInitial)
                EditorGUILayout.PropertyField(endValueProp, new GUIContent("End Value"), true);

            EditorGUILayout.PropertyField(interpolationProp, true);
            EditorGUILayout.PropertyField(discreteProp, true);
        }

        private int CreateEntryAt(SerializedProperty entriesProp, float time)
        {
            int newIdx = entriesProp.arraySize;
            entriesProp.InsertArrayElementAtIndex(newIdx);
            var entry = entriesProp.GetArrayElementAtIndex(newIdx);

            entry.FindPropertyRelative(nameof(AnimatedValueTrackEntry.StartTime)).floatValue = Mathf.Max(0f, time);
            entry.FindPropertyRelative(nameof(AnimatedValueTrackEntry.Duration)).floatValue = 1f;

            var animatorProp = entry.FindPropertyRelative(nameof(AnimatedValueTrackEntry.Animator));
            animatorProp.managedReferenceValue = new EasedAnimator
            {
                Ease = EaseType.InOutQuad,
                StartMode = EasedStartMode.StartFromCurrent,
                EndMode = EasedEndMode.EndAtAbsolute,
            };

            return newIdx;
        }

        private SerializedProperty GetEntriesProp(int trackIndex)
        {
            if (trackIndex < 0 || trackIndex >= _tracksProp.arraySize)
                return null;

            return _tracksProp.GetArrayElementAtIndex(trackIndex).FindPropertyRelative("_entries");
        }

        private void DrawBindablePropertyPicker(SerializedProperty bindablePropertyProp)
        {
            var lineRect = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
            var fieldRect = EditorGUI.PrefixLabel(lineRect, new GUIContent("Property"));

            var selectedProperty = GetBindableProperty(bindablePropertyProp);
            var label = GetBindablePropertyDisplayLabel(selectedProperty);
            var tooltip = GetBindablePropertyDisplayTooltip(selectedProperty);

            if (EditorGUI.DropdownButton(fieldRect, new GUIContent(label, tooltip), FocusType.Keyboard))
                ShowBindablePropertyMenu(bindablePropertyProp, selectedProperty);
        }

        private void ShowBindablePropertyMenu(SerializedProperty bindablePropertyProp, in BindableProperty selectedProperty)
        {
            var sequence = (TweenSequence)target;
            var menu = new GenericMenu();

            var properties = new List<BindableProperty>();
            BindablePropertyUtility.GetBindableProperties(sequence.gameObject, properties, recursive: true);
            properties.Sort((a, b) => string.Compare(GetMenuPath(sequence.gameObject, a), GetMenuPath(sequence.gameObject, b), StringComparison.Ordinal));

            for (int i = 0; i < properties.Count; i++)
            {
                var candidate = properties[i];
                string path = GetMenuPath(sequence.gameObject, candidate);
                var icon = ResolveBindablePropertyIcon(candidate);
                bool isCurrent = candidate.Equals(selectedProperty);

                menu.AddItem(new GUIContent(path, icon), isCurrent, () =>
                {
                    SetBindableProperty(bindablePropertyProp, candidate);
                    serializedObject.ApplyModifiedProperties();
                    sequence.MarkBidingsDirty();
                    sequence.MarkTimingDirty();
                    if (_previewing)
                        SamplePreview();
                    Repaint();
                });
            }

            if (properties.Count == 0)
                menu.AddDisabledItem(new GUIContent("(No bindable properties found)"));

            menu.ShowAsContext();
        }

        private static string GetMenuPath(GameObject root, in BindableProperty property)
        {
            var target = property.Target;
            if (target is Component component)
            {
                return $"{GetHierarchyPath(root.transform, component.transform)}/{ObjectNames.NicifyVariableName(component.GetType().Name)}/{ObjectNames.NicifyVariableName(property.Path)}";
            }

            if (target is GameObject gameObject)
                return $"{GetHierarchyPath(root.transform, gameObject.transform)}/(GameObject)/{ObjectNames.NicifyVariableName(property.Path)}";

            return $"(Unknown)/{ObjectNames.NicifyVariableName(property.Path)}";
        }

        private static string GetHierarchyPath(Transform root, Transform target)
        {
            if (root == null || target == null)
                return target != null ? target.name : "(Missing)";

            if (target == root)
                return root.name;

            var names = new Stack<string>();
            var current = target;
            while (current != null && current != root)
            {
                names.Push(current.name);
                current = current.parent;
            }

            if (current == root)
                names.Push(root.name);

            return string.Join("/", names);
        }

        private static Texture ResolveBindablePropertyIcon(in BindableProperty property)
        {
            if (property.Target is Component component)
                return EditorGUIUtility.ObjectContent(component, component.GetType()).image;

            if (property.Target is GameObject go)
                return EditorGUIUtility.ObjectContent(go, typeof(GameObject)).image;

            return EditorGUIUtility.IconContent("d_Settings").image;
        }

        private static string GetBindablePropertyDisplayLabel(in BindableProperty property)
        {
            if (property.Target == null || string.IsNullOrEmpty(property.Path))
                return "(select property)";

            if (property.Target is Component component)
            {
                string go = component.gameObject != null ? component.gameObject.name : "(missing GO)";
                string comp = ObjectNames.NicifyVariableName(component.GetType().Name);
                string prop = ObjectNames.NicifyVariableName(property.Path);
                return $"{go} → {comp} → {prop}";
            }

            if (property.Target is GameObject goTarget)
                return $"{goTarget.name} → {ObjectNames.NicifyVariableName(property.Path)}";

            return ObjectNames.NicifyVariableName(property.Path);
        }

        private static string GetBindablePropertyDisplayTooltip(in BindableProperty property)
        {
            if (property.Target == null || string.IsNullOrEmpty(property.Path))
                return "Choose a bindable property.";

            return $"{property.Target.name}.{property.Path} ({property.Kind})";
        }

        private static BindableProperty GetBindableProperty(SerializedProperty bindablePropertyProp)
        {
            var target = bindablePropertyProp.FindPropertyRelative("_target").objectReferenceValue;
            var path = bindablePropertyProp.FindPropertyRelative("_path").stringValue;
            var kind = (ValueKind)bindablePropertyProp.FindPropertyRelative("_kind").enumValueIndex;
            var componentLayout = (ComponentLayout)bindablePropertyProp.FindPropertyRelative("_componentLayout").enumValueIndex;
            var c1 = bindablePropertyProp.FindPropertyRelative("_componentOnePath").stringValue;
            var c2 = bindablePropertyProp.FindPropertyRelative("_componentTwoPath").stringValue;
            var c3 = bindablePropertyProp.FindPropertyRelative("_componentThreePath").stringValue;
            var c4 = bindablePropertyProp.FindPropertyRelative("_componentFourPath").stringValue;

            return BindableProperty.CreateWithComponentLayout(target, path, kind, componentLayout, c1, c2, c3, c4);
        }

        private static void SetBindableProperty(SerializedProperty bindablePropertyProp, in BindableProperty property)
        {
            bindablePropertyProp.FindPropertyRelative("_target").objectReferenceValue = property.Target;
            bindablePropertyProp.FindPropertyRelative("_path").stringValue = property.Path ?? string.Empty;
            bindablePropertyProp.FindPropertyRelative("_kind").enumValueIndex = (int)property.Kind;
            bindablePropertyProp.FindPropertyRelative("_componentLayout").enumValueIndex = (int)property.ComponentLayout;
            bindablePropertyProp.FindPropertyRelative("_componentOnePath").stringValue = property.ComponentOnePath ?? string.Empty;
            bindablePropertyProp.FindPropertyRelative("_componentTwoPath").stringValue = property.ComponentTwoPath ?? string.Empty;
            bindablePropertyProp.FindPropertyRelative("_componentThreePath").stringValue = property.ComponentThreePath ?? string.Empty;
            bindablePropertyProp.FindPropertyRelative("_componentFourPath").stringValue = property.ComponentFourPath ?? string.Empty;
        }

        private string GetTrackDisplayName(SerializedProperty trackProp)
        {
            var target = GetTrackPropertyTarget(trackProp);
            string path = GetTrackPropertyPath(trackProp);

            string targetName = target != null ? target.name : "<missing>";
            if (string.IsNullOrEmpty(path))
                return $"{targetName}.<path>";
            return $"{targetName}.{path}";
        }

        private static Object GetTrackPropertyTarget(SerializedProperty trackProp)
        {
            return trackProp
                .FindPropertyRelative(nameof(AnimatedValueTrack.Property))
                .FindPropertyRelative("_target")
                .objectReferenceValue;
        }

        private static string GetTrackPropertyPath(SerializedProperty trackProp)
        {
            return trackProp
                .FindPropertyRelative(nameof(AnimatedValueTrack.Property))
                .FindPropertyRelative("_path")
                .stringValue;
        }

        private static bool TryGetValidProperty(AnimatedValueTrack track, out BindableProperty property)
        {
            property = default;
            if (track == null)
                return false;

            property = track.Property;
            return property.Target != null && !string.IsNullOrEmpty(property.Path);
        }

        private static Color GetEntryColor(bool isSelected, bool isDragging)
        {
            if (isDragging) return new Color(0.45f, 0.65f, 1f, 0.9f);
            if (isSelected) return new Color(0.55f, 0.75f, 1f, 0.85f);
            return new Color(0.35f, 0.55f, 0.85f, 0.85f);
        }

        private static void DrawOutline(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x - 1f, rect.yMax, rect.width + 2f, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x - 1f, rect.y - 1f, 1f, rect.height + 2f), color);
            EditorGUI.DrawRect(new Rect(rect.xMax, rect.y - 1f, 1f, rect.height + 2f), color);
        }

        private static string GetAnimatorShortName(SerializedProperty animProp)
        {
            var a = animProp.managedReferenceValue as ValueAnimator;
            if (a is EasedAnimator) return "Ease";
            if (a is ShakeAnimator) return "Shake";
            if (a is PunchAnimator) return "Punch";
            return a?.GetType().Name ?? "?";
        }

        private void DrawPlayhead(Rect content, Rect ruler, float duration)
        {
            float x = content.x + (_playheadTime / duration) * content.width;
            x = Mathf.Clamp(x, content.x, content.xMax);
            EditorGUI.DrawRect(new Rect(x - 0.5f, content.y, 1f, content.height), new Color(1f, 0.5f, 0.2f, 1f));
            EditorGUI.DrawRect(new Rect(x - 5f, ruler.yMax - 6f, 10f, 6f), new Color(1f, 0.5f, 0.2f, 1f));
        }

        private static float MouseToTime(float mouseX, Rect rect, float duration)
        {
            float u = Mathf.Clamp01((mouseX - rect.x) / rect.width);
            return u * duration;
        }

        private static void DrawRulerTicks(Rect ruler, Rect content, float duration)
        {
            int seconds = Mathf.CeilToInt(duration);
            for (int s = 0; s <= seconds; s++)
            {
                float x = ruler.x + (s / duration) * ruler.width;
                EditorGUI.DrawRect(new Rect(x, ruler.yMax - 5f, 1f, 5f), new Color(1f, 1f, 1f, 0.35f));
                EditorGUI.DrawRect(new Rect(x, content.y, 1f, content.height), new Color(1f, 1f, 1f, 0.06f));
                GUI.Label(new Rect(x + 2f, ruler.y + 2f, 40f, 16f), $"{s}s", EditorStyles.miniLabel);
            }
        }

        private void HandleTimelineInteraction(Rect ruler, Rect content, float duration)
        {
            int id = GUIUtility.GetControlID(TimelineControlId, FocusType.Passive);
            var e = Event.current;

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button != 0)
                        break;

                    if (TryGetTopmostEntryHit(e.mousePosition, out var hit))
                    {
                        BeginEntryDrag(id, hit, e.mousePosition, duration);
                        e.Use();
                        break;
                    }

                    // if (ruler.Contains(e.mousePosition))
                    if (ruler.Contains(e.mousePosition) || content.Contains(e.mousePosition))
                    {
                        BeginPlayheadDrag(id, content, duration, e.mousePosition);
                        e.Use();
                        break;
                    }

                    //still just capture control:
                    {
                        ClearSelectedEntry();
                        GUIUtility.hotControl = id;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id)
                        break;

                    if (_dragKind == TimelineDragKind.Playhead)
                    {
                        SetPlayhead(MouseToTime(e.mousePosition.x, content, duration), true);
                        e.Use();
                    }
                    else if (_dragKind != TimelineDragKind.None && _dragTrackIndex >= 0 && _dragEntryIndex >= 0)
                    {
                        UpdateEntryDrag(e.mousePosition, duration);
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id)
                        break;

                    if (_dragKind == TimelineDragKind.Playhead)
                    {
                        EndTimelineDrag();
                        e.Use();
                        break;
                    }

                    DeleteZeroDurationAndEmptyTracks();
                    EndTimelineDrag();
                    e.Use();
                    GUIUtility.ExitGUI();
                    break;
            }
        }

        private bool TryGetTopmostEntryHit(Vector2 mousePosition, out EntryHit hit)
        {
            for (int i = _entryHits.Count - 1; i >= 0; i--)
            {
                if (_entryHits[i].Bar.Contains(mousePosition))
                {
                    hit = _entryHits[i];
                    return true;
                }
            }

            hit = default;
            return false;
        }

        private void BeginEntryDrag(int id, EntryHit hit, Vector2 mousePosition, float duration)
        {
            SelectEntry(hit.TrackIndex, hit.EntryIndex);

            GUIUtility.hotControl = id;
            _dragKind = hit.RightEdge.Contains(mousePosition)
                ? TimelineDragKind.EntryResizeEnd
                : hit.LeftEdge.Contains(mousePosition)
                    ? TimelineDragKind.EntryResizeStart
                    : TimelineDragKind.EntryMove;

            _dragTrackIndex = hit.TrackIndex;
            _dragEntryIndex = hit.EntryIndex;
            _dragRowRect = hit.RowRect;

            if (TryGetEntryTimingProperties(hit.TrackIndex, hit.EntryIndex, out var startTimeProp, out var durProp))
            {
                _dragOriginalStart = startTimeProp.floatValue;
                _dragOriginalDuration = durProp.floatValue;
                _dragGhostStart = _dragOriginalStart;
                _dragGhostEnd = _dragOriginalStart + _dragOriginalDuration;
                _dragGrabOffset = MouseToTime(mousePosition.x, hit.RowRect, duration) - _dragOriginalStart;
            }
            else
            {
                EndTimelineDrag();
            }
        }

        private void BeginPlayheadDrag(int id, Rect content, float duration, Vector2 mousePosition)
        {
            ClearSelectedEntry();

            GUIUtility.hotControl = id;
            _dragKind = TimelineDragKind.Playhead;
            _dragTrackIndex = -1;
            _dragEntryIndex = -1;
            _dragRowRect = default;
            SetPlayhead(MouseToTime(mousePosition.x, content, duration), true);
        }

        private void UpdateEntryDrag(Vector2 mousePosition, float duration)
        {
            if (!TryGetEntryTimingProperties(_dragTrackIndex, _dragEntryIndex, out var startTimeProp, out var durProp))
                return;

            float mouseTime = MouseToTime(mousePosition.x, _dragRowRect, duration);
            float snappedTime = ApplySnap(mouseTime, duration, _dragRowRect);

            switch (_dragKind)
            {
                case TimelineDragKind.EntryMove:
                {
                    float naturalStart = Mathf.Max(0f, snappedTime - _dragGrabOffset);
                    float naturalEnd = naturalStart + _dragOriginalDuration;
                    GetNeighborBounds(_dragTrackIndex, _dragEntryIndex, naturalStart, naturalEnd, out float leftBound, out float rightBound);
                    float effectiveStart = Mathf.Max(naturalStart, leftBound);
                    float effectiveEnd = Mathf.Min(naturalEnd, rightBound);
                    startTimeProp.floatValue = effectiveStart;
                    durProp.floatValue = Mathf.Max(0f, effectiveEnd - effectiveStart);
                    _dragGhostStart = naturalStart;
                    _dragGhostEnd = naturalEnd;
                    break;
                }
                case TimelineDragKind.EntryResizeStart:
                {
                    float pinnedEnd = _dragOriginalStart + _dragOriginalDuration;
                    GetNeighborBounds(_dragTrackIndex, _dragEntryIndex, snappedTime, pinnedEnd, out float leftBound, out _);
                    float newStart = Mathf.Clamp(snappedTime, leftBound, pinnedEnd);
                    startTimeProp.floatValue = newStart;
                    durProp.floatValue = Mathf.Max(0f, pinnedEnd - newStart);
                    _dragGhostStart = snappedTime;
                    _dragGhostEnd = pinnedEnd;
                    break;
                }
                case TimelineDragKind.EntryResizeEnd:
                {
                    float currentStart = startTimeProp.floatValue;
                    GetNeighborBounds(_dragTrackIndex, _dragEntryIndex, currentStart, snappedTime, out _, out float rightBound);
                    float newEnd = Mathf.Clamp(snappedTime, currentStart, rightBound);
                    durProp.floatValue = Mathf.Max(0f, newEnd - currentStart);
                    _dragGhostStart = currentStart;
                    _dragGhostEnd = snappedTime;
                    break;
                }
            }

            ApplyTimingChanges();
        }

        private bool TryGetEntryTimingProperties(int trackIdx, int entryIdx, out SerializedProperty startTimeProp, out SerializedProperty durProp)
        {
            startTimeProp = null;
            durProp = null;

            var entriesProp = GetEntriesProp(trackIdx);
            if (entriesProp == null || entryIdx < 0 || entryIdx >= entriesProp.arraySize)
                return false;

            var entryProp = entriesProp.GetArrayElementAtIndex(entryIdx);
            startTimeProp = entryProp.FindPropertyRelative(nameof(AnimatedValueTrackEntry.StartTime));
            durProp = entryProp.FindPropertyRelative(nameof(AnimatedValueTrackEntry.Duration));
            return startTimeProp != null && durProp != null;
        }

        private void SelectEntry(int trackIdx, int entryIdx)
        {
            _selectedTrackIndex = trackIdx;
            _selectedEntryIndex = entryIdx;
        }

        private void ClearSelectedEntry()
        {
            _selectedTrackIndex = -1;
            _selectedEntryIndex = -1;
        }

        private void AdjustSelectionAfterEntryDeletion(int trackIdx, int deletedEntryIdx)
        {
            if (_selectedTrackIndex != trackIdx)
                return;

            if (_selectedEntryIndex == deletedEntryIdx)
            {
                ClearSelectedEntry();
                return;
            }

            if (_selectedEntryIndex > deletedEntryIdx)
                _selectedEntryIndex--;
        }

        private void AdjustSelectionAfterTrackDeletion(int deletedTrackIdx)
        {
            if (_selectedTrackIndex == deletedTrackIdx)
            {
                ClearSelectedEntry();
                return;
            }

            if (_selectedTrackIndex > deletedTrackIdx)
                _selectedTrackIndex--;
        }

        private void CollectSnapPoints(float duration, Rect contentRect)
        {
            _snapPoints.Clear();
            CollectSnapPointValues(_snapPoints);

            float snapThresholdSec = SnapThresholdPx / contentRect.width * duration;
            float yMin = contentRect.y;
            float yMax = contentRect.yMax;

            var de = GetEntriesProp(_dragTrackIndex)?.GetArrayElementAtIndex(_dragEntryIndex);
            if (de == null) return;

            float dragStart = de.FindPropertyRelative(nameof(AnimatedValueTrackEntry.StartTime)).floatValue;
            float dragDur = de.FindPropertyRelative(nameof(AnimatedValueTrackEntry.Duration)).floatValue;
            float dragEnd = dragStart + dragDur;

            foreach (float p in _snapPoints)
            {
                if (_dragKind == TimelineDragKind.EntryMove)
                {
                    if (Mathf.Abs(dragStart - p) <= snapThresholdSec)
                        _snapLines.Add((p, yMin, yMax));
                }
                else if (_dragKind == TimelineDragKind.EntryResizeEnd)
                {
                    if (Mathf.Abs(dragEnd - p) <= snapThresholdSec)
                        _snapLines.Add((p, yMin, yMax));
                }
            }
        }

        private float ApplySnap(float time, float duration, Rect timeRect)
        {
            if (_dragTrackIndex < 0 || _dragEntryIndex < 0)
                return time;

            float snapThresholdSec = SnapThresholdPx / timeRect.width * duration;
            float bestSnap = time;
            float bestDist = snapThresholdSec;

            _snapPoints.Clear();
            CollectSnapPointValues(_snapPoints);
            foreach (float point in _snapPoints)
            {
                float distance = Mathf.Abs(time - point);
                if (distance < bestDist)
                {
                    bestDist = distance;
                    bestSnap = point;
                }
            }

            return bestSnap;
        }

        private void CollectSnapPointValues(HashSet<float> points)
        {
            var seq = (TweenSequence)target;
            for (int ti = 0; ti < seq.Tracks.Count; ti++)
            {
                var track = seq.Tracks[ti];
                if (track == null) continue;

                for (int ei = 0; ei < track.Entries.Count; ei++)
                {
                    if (ti == _dragTrackIndex && ei == _dragEntryIndex)
                        continue;

                    var entry = track.Entries[ei];
                    if (entry == null) continue;

                    points.Add(entry.StartTime);
                    points.Add(entry.StartTime + entry.Duration);
                }
            }
        }
    }
}
