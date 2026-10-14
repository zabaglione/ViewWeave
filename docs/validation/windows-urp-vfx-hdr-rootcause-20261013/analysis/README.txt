Read-only analysis of frozen Phase B and Phase C paired-output artifacts.

Run from the repository root with:
  python docs/validation/windows-urp-vfx-hdr-rootcause-20261013/analysis/analyze_hdr_probe_pairs.py

The analysis reads raw float32 RGBA payloads, selected paired PNGs, and Green frame CSV files. It does not edit inputs or invoke Unity. Input byte counts and SHA256 digests are recorded in analysis.json and manifest.json.

The quilt target is recorded as RGBA8 sRGB and the normal target as linear RGBA16F. The quilt float readback is treated as normalized sRGB code values and decoded with the piecewise inverse-sRGB function before derived linear comparisons. This is an encoding-based analysis assumption, corroborated by the recorded target descriptors and controlled GL.sRGBWrite setting; it is not an independent colorimeter calibration. No clamp or normalization is applied beyond the stored RGB8 range.

The coin halo ROI uses bottom-origin coordinates: x=[145,185) and [295,335), y=[300,345), totaling 3600 pixels. Raw Unity float payloads are treated as bottom-row-first; PNG images are top-row-first and mapped to the same bottom-origin ROI.

The Green CSV reports native particle alive counts separately from pixel-mask detections. The selected float-pair green-dominant mask is descriptive and is not used as proof of VFX production. Cross-format Color32 changed-pixel counts are not normalized comparisons. Results apply only to the frozen Windows D3D12, Unity Play Mode, one-view, AA-off, post-processing-on clone observations; no hardware device, user project, or production SDK fix was tested.
