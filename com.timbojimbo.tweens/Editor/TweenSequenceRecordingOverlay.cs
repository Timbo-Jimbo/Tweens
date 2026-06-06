using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace TimboJimboEditor
{
    [Overlay(typeof(SceneView), OverlayId, "Tween Recording", defaultDisplay = true)]
    [Icon("d_Animation.Record")]
    internal sealed class TweenSequenceRecordingOverlay : IMGUIOverlay, ITransientOverlay
    {
        private const string OverlayId = "tween-sequence-recording-overlay";
        private const int MaxVisibleEvents = 6;

        private readonly List<string> _events = new();

        public bool visible => TweenSequenceRecordingSession.IsRecording;

        public override void OnGUI()
        {
            if (!TweenSequenceRecordingSession.IsRecording)
                return;

            var target = TweenSequenceRecordingSession.Target;
            TweenSequenceRecordingSession.CopyRecentEvents(_events);

            using (new GUILayout.VerticalScope(GUILayout.MinWidth(320f)))
            {
                GUILayout.Label("Tween Recording", EditorStyles.boldLabel);

                string targetName = target != null ? target.name : "(missing)";
                GUILayout.Label($"Target: {targetName}", EditorStyles.miniLabel);
                GUILayout.Label($"Playhead: {TweenSequenceRecordingSession.PlayheadTime:0.###}s", EditorStyles.miniLabel);

                GUILayout.Space(4f);

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("End Recording"))
                        TweenSequenceRecordingSession.Stop();

                    using (new EditorGUI.DisabledScope(target == null))
                    {
                        if (GUILayout.Button("Select Sequence") && target != null)
                            Selection.activeObject = target;
                    }
                }

                GUILayout.Space(4f);
                GUILayout.Label("Latest events", EditorStyles.miniBoldLabel);

                if (_events.Count == 0)
                {
                    GUILayout.Label("Waiting for edits…", EditorStyles.miniLabel);
                }
                else
                {
                    int count = Mathf.Min(MaxVisibleEvents, _events.Count);
                    for (int i = 0; i < count; i++)
                        GUILayout.Label($"• {_events[i]}", EditorStyles.miniLabel);
                }
            }
        }
    }
}
