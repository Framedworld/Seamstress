using System.Numerics;
using System.Text.Json;
using Seamstress.Core;
using Seamstress.Core.Animation;
using Seamstress.Core.Model;

namespace Seamstress.App;

/// <summary>
/// Avatar animation in the 3D view: pick a built-in pose change or a clip from a rigged file, play it while the
/// garments simulate on the moving body, pause, scrub through what was recorded, change the speed, export the
/// recording, and reset to the drape from before playback.
/// </summary>
public partial class AppController
{
    private BodyAnimation? _animation;
    private Seamstress.Core.Simulation.Avatar? _animationAvatar;
    private string _animationId = BuiltInAnimations.TPoseToAPose;
    private readonly List<(string Id, string Name)> _animationFiles = new();
    private Dictionary<string, (float[]? Drape, string? Key)>? _drapeBeforeAnimation;
    private float _animationSpeed = 1f;
    private float _scrubTime = -1;
    private long _animationBodySeen;
    private int _animationRevision;
    private bool _animationWasPlaying;

    /// <summary>Goes up whenever anything about playback changes, so the animation bar re-reads it.</summary>
    public int AnimationRevision { get => _animationRevision; private set { _animationRevision = value; Notify(); } }

    /// <summary>The animations that can be chosen, as JSON: [{id, name}].</summary>
    public string AnimationChoices()
    {
        var list = BuiltInAnimations.Ids.Select(id => new { id, name = BuiltInName(id) })
            .Concat(_animationFiles.Select(f => new { id = f.Id, name = f.Name }));
        return JsonSerializer.Serialize(list);
    }

    private static string BuiltInName(string id) => id switch
    {
        BuiltInAnimations.TPoseToAPose => Lang.T("T-pose to A-pose"),
        BuiltInAnimations.ArmRaise => Lang.T("Arm raise"),
        _ => id,
    };

    public string AnimationId
    {
        get => _animationId;
        set
        {
            if (value == _animationId)
                return;
            StopAnimation();
            _animationId = value;
            AnimationRevision++;
        }
    }

    public bool AnimationActive => _animation != null;
    public bool AnimationPlaying => _animation != null && ReferenceEquals(_simulation.Motion, _animation);
    public double AnimationTime => _animation == null ? 0 : _scrubTime >= 0 ? _scrubTime : _animation.Time;
    public double AnimationDuration => _animation?.Duration ?? 0;
    public double AnimationRecorded => _animation?.Recording.RecordedDuration ?? 0;

    public double AnimationSpeed
    {
        get => _animationSpeed;
        set
        {
            _animationSpeed = (float)Math.Clamp(value, 0.05, 4);
            if (_animation != null)
                _animation.Speed = _animationSpeed;
            AnimationRevision++;
        }
    }

    /// <summary>Plays from where the timeline is, or pauses.</summary>
    public void PlayAnimation()
    {
        if (AnimationPlaying)
        {
            PauseAnimation();
            return;
        }
        if (_avatar == null)
        {
            StatusText = Lang.T("Load an avatar first: the animation moves the avatar.");
            return;
        }
        try
        {
            if (_animation == null)
            {
                var id = _animationId;
                string source = id;
                string? clip = null;
                if (id.StartsWith("file|", StringComparison.Ordinal))
                {
                    var parts = id.Split('|', 3);
                    source = parts[1];
                    clip = parts.Length > 2 ? parts[2] : null;
                }
                _animation = AnimationLibrary.Prepare(_avatar, _workspace.Document.Avatar, source, clip);
                _animationAvatar = _avatar;
                _animation.Speed = _animationSpeed;
                _drapeBeforeAnimation = _workspace.Document.Garments.SelectMany(g => g.Pieces).ToDictionary(p => p.Id, p => (p.Drape, p.DrapeKey));
                _scrubTime = -1;
            }
            if (_animation.Finished && _scrubTime < 0)
            {
                StatusText = Lang.T("The animation has played to the end. Drag the timeline back, or press Reset.");
                return;
            }
            if (_scrubTime >= 0)
            {
                // Carry on from the recorded frame being shown.
                var frame = _animation.Recording.At(_scrubTime);
                float from = frame?.Time ?? 0;
                _animation.Recording.TruncateAfter(from);
                _animation.Seek(from);
                _scrubTime = -1;
            }
            string? problem = _simulation.Start(allGarments: true, Lang.F("Play {0}", _animation.Source), null, _animation, _animation.Field.Grid);
            if (problem != null)
            {
                StatusText = problem;
                return;
            }
            StatusText = Lang.F("Playing {0} — the garments follow the body.", _animation.Source);
            AppLog.Write($"Playing animation {_animation.Source} on {_simulation.ParticleCount} particles.");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or InvalidOperationException)
        {
            _animation = null;
            Fail(Lang.T("Could not play the animation."), e);
        }
        Notify(nameof(Simulating));
        AnimationRevision++;
    }

    private void PauseAnimation()
    {
        if (!AnimationPlaying)
            return;
        string? error = _simulation.Stop();
        if (error != null)
            Fail(Lang.T("The simulation stopped because of an error."), new Exception(error));
        else
            StatusText = Lang.T("Animation paused. Drag the timeline to look back through the recording.");
        Notify(nameof(Simulating));
        AnimationRevision++;
    }

    /// <summary>Ends playback and puts the garments and the avatar back the way they were before it started.</summary>
    public void StopAnimation()
    {
        if (_animation == null)
            return;
        if (AnimationPlaying)
            _simulation.Stop();
        var before = _drapeBeforeAnimation;
        _animation = null;
        _drapeBeforeAnimation = null;
        _scrubTime = -1;
        _viewport.PosedAvatar = null;
        if (before != null)
        {
            _workspace.History.Execute(Lang.T("Reset animation"), doc =>
            {
                foreach (var piece in doc.Garments.SelectMany(g => g.Pieces))
                {
                    if (before.TryGetValue(piece.Id, out var was))
                        (piece.Drape, piece.DrapeKey) = was;
                }
            });
        }
        StatusText = Lang.T("Animation reset: the garments are back in the drape from before it played.");
        Notify(nameof(Simulating));
        ViewportRevision++;
        AnimationRevision++;
    }

    /// <summary>Shows the recorded frame nearest <paramref name="seconds"/> (pausing playback first).</summary>
    public void ScrubAnimation(double seconds)
    {
        if (_animation == null)
            return;
        if (AnimationPlaying)
            PauseAnimation();
        var recording = _animation.Recording;
        var frame = recording.At((float)seconds);
        if (frame == null)
            return;
        _scrubTime = frame.Time;
        var layout = recording.Layout;
        _workspace.History.Execute(Lang.T("Scrub animation"), doc =>
        {
            foreach (var piece in layout)
            {
                var target = doc.Garments.SelectMany(g => g.Pieces).FirstOrDefault(p => p.Id == piece.PieceId);
                if (target != null)
                    target.Drape = recording.DrapeOf(frame, piece);
            }
        }, mergeKey: "animation-scrub");
        _viewport.PosedAvatar = frame.Body;
        ViewportRevision++;
        AnimationRevision++;
    }

    /// <summary>Adds the clips of a rigged FBX or glTF file to the choices and picks the first. Returns false on failure.</summary>
    public bool LoadAnimationFile(string url)
    {
        string path = LocalPath(url);
        try
        {
            var names = AnimationLibrary.ClipNames(path);
            if (names.Count == 0)
            {
                StatusText = Lang.F("“{0}” has no animation in it.", Path.GetFileName(path));
                return false;
            }
            _animationFiles.RemoveAll(f => f.Id.StartsWith("file|" + path + "|", StringComparison.Ordinal));
            foreach (string name in names)
                _animationFiles.Add(($"file|{path}|{name}", $"{Path.GetFileNameWithoutExtension(path)}: {name}"));
            AnimationId = _animationFiles.First(f => f.Id.StartsWith("file|" + path + "|", StringComparison.Ordinal)).Id;
            StatusText = Lang.F("Loaded {0} animations from “{1}”.", names.Count, Path.GetFileName(path));
            AnimationRevision++;
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
        {
            Fail(Lang.T("Could not read the animation file."), e);
            return false;
        }
    }

    /// <summary>Writes what was recorded as a glTF point cache (morph target per frame). Returns false on failure.</summary>
    public bool ExportAnimationRecording(string url)
    {
        if (_animation == null || _animation.Recording.Frames.Count == 0)
        {
            StatusText = Lang.T("Play the animation first: there is nothing recorded to export.");
            return false;
        }
        string path = LocalPath(url);
        if (!path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            path += ".glb";
        try
        {
            if (AnimationPlaying)
                PauseAnimation();
            var result = PointCacheExporter.Write(path, _animation.Recording, _workspace);
            StatusText = Lang.F("Exported {0} frames to “{1}”.", result.Frames, Path.GetFileName(path));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Fail(Lang.T("Could not export the recording."), e);
            return false;
        }
    }

    /// <summary>Runs frames of a playing animation at once, without waiting for the clock (the smoke test).</summary>
    public void AnimationRunFrames(int frames)
    {
        if (!AnimationPlaying)
            return;
        _simulation.RunFrames(frames);
        Tick();
    }

    /// <summary>Called from <see cref="Tick"/>: shows the body where the simulation has it and pauses at the end.</summary>
    private void AnimationTick()
    {
        var anim = _animation;
        if (anim == null)
            return;
        if (!ReferenceEquals(_animationAvatar, _avatar))
        {
            // Another avatar was loaded: the animation belonged to the old one.
            StopAnimation();
            return;
        }
        bool playing = AnimationPlaying;
        if (playing && anim.TakeBody(ref _animationBodySeen) is { } body)
        {
            _viewport.PosedAvatar = body;
            ViewportRevision++;
            AnimationRevision++;
        }
        if (playing && anim.Finished)
        {
            PauseAnimation();
            StatusText = Lang.T("The animation has played to the end. Export the recording, drag back through it, or press Reset.");
        }
        if (playing != _animationWasPlaying)
        {
            _animationWasPlaying = playing;
            AnimationRevision++;
        }
    }
}
