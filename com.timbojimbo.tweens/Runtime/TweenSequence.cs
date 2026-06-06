using System.Collections.Generic;
using TimboJimbo.PropertyBindings;
using UnityEngine;

namespace TimboJimbo
{
    public enum TweenSequenceState
    {
        Stopped,
        Playing,
        Paused
    }

    [AddComponentMenu("TimboJimbo/Tween Sequence")]
    public class TweenSequence : MonoBehaviour
    {
        [SerializeField] private List<AnimatedValueTrack> _tracks = new List<AnimatedValueTrack>();
        [SerializeField] private bool _playOnEnable;
        [SerializeField] private bool _loop;

        private float _time;
        private TweenSequenceState _state = TweenSequenceState.Stopped;
        private float _lastSampledTime = -1f;

        private readonly Dictionary<BindableProperty, ValueContainer> _baselines = new Dictionary<BindableProperty, ValueContainer>(BindablePropertyEqualityComparer.Instance);
        private bool _baselinesCaptured;

        private PropertyBindingCollection _bindings;
        private bool _bindingsDirty = true;

        private readonly Dictionary<AnimatedValueTrackEntry, TrackTimeSlot> _timeSlots = new Dictionary<AnimatedValueTrackEntry, TrackTimeSlot>();
        private bool _timeSlotsDirty = true;
        private float _duration;

        public IReadOnlyList<AnimatedValueTrack> Tracks => _tracks;
        public TweenSequenceState State => _state;
        public float Time => _time;

        public float Duration
        {
            get
            {
                EnsureTimeSlots();
                return _duration;
            }
        }

        public bool Loop { get => _loop; set => _loop = value; }

        public void AddTrack(AnimatedValueTrack track)
        {
            _tracks.Add(track);
            _bindingsDirty = true;
            InvalidateTimeSlots();
        }

        public void RemoveTrackAt(int index)
        {
            if (index < 0 || index >= _tracks.Count) return;
            _tracks.RemoveAt(index);
            _bindingsDirty = true;
            InvalidateTimeSlots();
        }

        public void InvalidateBindings()
        {
            _bindingsDirty = true;
            _baselinesCaptured = false;
        }

        public void InvalidateTimeSlots()
        {
            _timeSlotsDirty = true;
            for (int i = 0; i < _tracks.Count; i++)
                _tracks[i]?.InvalidateSort();
        }

        public void InvalidateSort() => InvalidateTimeSlots();

        public bool TryGetTimeSlot(AnimatedValueTrackEntry entry, out TrackTimeSlot slot)
        {
            EnsureTimeSlots();
            if (entry == null)
            {
                slot = default;
                return false;
            }
            return _timeSlots.TryGetValue(entry, out slot);
        }

        public void CaptureBaselines()
        {
            EnsureBindings();
            _baselines.Clear();
            for (int i = 0; i < _tracks.Count; i++)
            {
                var track = _tracks[i];
                if (!TryGetValidProperty(track, out var property)) continue;
                if (_baselines.ContainsKey(property)) continue;

                if (_bindings != null && _bindings.TryRead(property, out var baseline))
                    _baselines[property] = baseline;
                else
                    _baselines[property] = ValueContainer.FromDefault(property.Kind);
            }
            _baselinesCaptured = true;
        }

        public void ClearBaselines()
        {
            _baselines.Clear();
            _baselinesCaptured = false;
        }

        public void SeedBaseline(BindableProperty property, ValueContainer value)
        {
            if (property.Target == null) return;
            _baselines[property] = value;
            _baselinesCaptured = true;
        }

        public bool TryGetBaseline(AnimatedValueTrack track, out ValueContainer value)
        {
            if (!TryGetValidProperty(track, out var property))
            {
                value = default;
                return false;
            }

            return _baselines.TryGetValue(property, out value);
        }

        public void Play()
        {
            if (_state == TweenSequenceState.Stopped)
            {
                _time = 0f;
                _lastSampledTime = -1f;
                CaptureBaselines();
            }
            _state = TweenSequenceState.Playing;
        }

        public void Pause()
        {
            if (_state == TweenSequenceState.Playing)
                _state = TweenSequenceState.Paused;
        }

        public void Stop()
        {
            _state = TweenSequenceState.Stopped;
            _time = 0f;
            _lastSampledTime = -1f;
            ClearBaselines();
        }

        public void SetTime(float time)
        {
            _time = time;
        }

        private void OnEnable()
        {
            if (_playOnEnable)
                Play();
        }

        private void OnDisable()
        {
            _bindings?.Dispose();
            _bindings = null;
        }

        private void Update()
        {
            if (_state != TweenSequenceState.Playing) return;
            Advance(UnityEngine.Time.deltaTime);
        }

        public void Advance(float deltaTime)
        {
            _time += deltaTime;
            float duration = Duration;
            if (_loop && duration > 0f)
            {
                while (_time >= duration)
                {
                    _time -= duration;
                    _lastSampledTime = -1f;
                }
            }
            else if (_time >= duration)
            {
                _time = duration;
                Sample(_time);
                _state = TweenSequenceState.Stopped;
                return;
            }
            Sample(_time);
        }

        public void Sample(float time)
        {
            EnsureBindings();

            if (!_baselinesCaptured)
                CaptureBaselines();

            EnsureTimeSlots();

            if (_bindings != null)
            {
                using var bulkWriter = _bindings.StartBulkWriteScope();

                for (int i = 0; i < _tracks.Count; i++)
                {
                    var track = _tracks[i];
                    if (!TryGetValidProperty(track, out var property)) continue;

                    if (!_baselines.TryGetValue(property, out var baseline))
                    {
                        if (_bindings.TryRead(property, out var currentValue))
                            baseline = currentValue;
                        else
                            baseline = ValueContainer.FromDefault(property.Kind);

                        _baselines[property] = baseline;
                    }

                    var value = track.Evaluate(time, baseline, _timeSlots);
                    bulkWriter.TryWrite(property, value);
                }
            }

            _lastSampledTime = time;
        }

        private void EnsureTimeSlots()
        {
            if (!_timeSlotsDirty) return;
            _duration = TrackTimeSlotResolver.Resolve(_tracks, _timeSlots);
            _timeSlotsDirty = false;
        }

        private void EnsureBindings()
        {
            if (!_bindingsDirty && _bindings != null)
                return;

            _bindings?.Dispose();

            var properties = new List<BindableProperty>();
            var seen = new HashSet<BindableProperty>(BindablePropertyEqualityComparer.Instance);
            for (int i = 0; i < _tracks.Count; i++)
            {
                if (!TryGetValidProperty(_tracks[i], out var property))
                    continue;

                if (seen.Add(property))
                    properties.Add(property);
            }

            _bindings = PropertyBindingCollection.Bind(gameObject, properties);
            _bindingsDirty = false;
        }

        private static bool TryGetValidProperty(AnimatedValueTrack track, out BindableProperty property)
        {
            property = default;
            if (track == null)
                return false;

            property = track.Property;
            return property.Target != null && !string.IsNullOrEmpty(property.Path);
        }
    }
}
