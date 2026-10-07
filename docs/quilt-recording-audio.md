# Quilt PNG and audio recording

ViewWeave can optionally record the Unity audio mix alongside a Quilt PNG sequence. Audio capture is opt-in; the existing `StartRecording(outputRoot, fps, frameCount)` API continues to create a silent PNG sequence. Call `StartRecordingWithAudio(...)` or choose **Start PNG Sequence + Audio** in **Window > ViewWeave > Quilt Capture** to request audio.

Audio capture is currently limited to Windows Editor Play mode and an already active Mono speaker configuration. ViewWeave checks the current configuration and rejects unsupported settings before starting. It never calls `AudioSettings.Reset` or changes the operating system or project audio configuration.

To record audio, stop Play mode and open **Edit > Project Settings > Audio**. Set **Default Speaker Mode** to **Mono**, then enter Play mode again and confirm that the current output configuration is Mono before starting audio capture. Mono is a one-channel speaker mode ([Unity Audio Manager settings](https://docs.unity3d.com/6000.6/Documentation/Manual/class-AudioManager.html), [AudioSpeakerMode.Mono](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/AudioSpeakerMode.html)). This project setting affects audio throughout the project; it is only needed when you choose audio recording. If you prefer to keep the existing setting, use the regular silent PNG recording.

Unity's `AudioRenderer` may mute normal audio output while it is active. It captures the active Unity mix, including playing or scheduled `AudioSource` content. ViewWeave does not change `AudioSource`, `AudioListener`, or listener pause and volume settings. Check the intended mix before recording; the exported WAV is the captured mix.

Each completed frame is paired with one `AudioRenderer.Render` result immediately after the quilt renderer's scheduler at the final `PostLateUpdate` boundary. The recorder verifies this hook and that the preceding Unity frame reached it while recording and paused. A frame is added only after its PNG and sidecar, PCM samples, and frame-log row have all been written. If a frame or file commit fails, temporary images and uncommitted PCM are discarded. Pause and resume preserve completed pairs; paused samples are discarded, and the resume frame is skipped for both streams. Stopping or leaving Play mode with an unfinished frame marks the recording incomplete.

ViewWeave calls `AudioRenderer.Stop` only after its own `Start` succeeded. If that stop returns false or throws, ViewWeave records an incomplete WAV, reports that shutdown is unconfirmed, and rejects further audio recordings until the Unity Editor is restarted. It does not retry `Stop`, reset the audio configuration, or unload the audio backend. A failed start does not stop another owner's renderer.

The recording folder contains:

- `frame_*.png` and matching JSON sidecars for completed quilts.
- `frames.jsonl`, with the Unity frame and the first sample, sample count, and exclusive last-sample index for each paired frame.
- `audio.wav` for a complete recording, or `audio.incomplete.wav` for an interrupted one. WAV data is mono IEEE float32 at the active output sample rate.
- `audio.json` and `manifest.json`, with the sample count and sample rate, audio and simulation durations, duration residual, and completion status.

Unity's DSP sample allocation can leave a small duration residual. A 150-frame, 30 FPS Mono probe measured 4.992 seconds of audio for 5 seconds of simulated video, an approximately -8 ms residual. This measures total duration only; it does not prove sample-accurate alignment at every frame. ViewWeave writes the samples Unity actually returns and does not stretch, pad, duplicate, or otherwise correct them. RIFF's 32-bit size limit is checked against bytes actually written; estimated duration is not used to reject a recording. Video muxing and audio encoding are not part of this feature.

The pipeline integration tests `UrpMonoAudioRecordingPairsPngFramesAndPauseResumeSamples` and `HdrpMonoAudioRecordingPairsPngFramesAndPauseResumeSamples` exercise actual quilts, pause/resume, WAV format, frame/sample ranges, and natural completion in Windows Editor Play mode with Mono audio output. `UrpMonoAudioPauseThenStopRestoresFilesAndCaptureTime`, `UrpMonoAudioPauseThenDisableRestoresFilesAndCaptureTime`, and `UrpAudioStartStopPreservesPlayingAndScheduledSources` cover cancellation cleanup and existing source state. Fake-backend tests inject an unconfirmed stop, hook loss, WAV-header initialization failure, frame-commit I/O failure, DSP sample counts above the nominal frame estimate, and duplicate boundaries without starting Unity's native audio renderer. These tests are not a substitute for checking the intended audio mix in a project.
