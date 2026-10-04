# 性能の実測と再現手順

現在の実測は、BridgeなしのWindows Development Player、Unity 6000.6.4f1、URP 17.6、Direct3D11、NVIDIA GeForce RTX 3090 Ti、graphics multi-threading有効で取得しています。固定128x96 tile、共有Lit mesh約75個、120 frame warmup、1000 frameを3 run使用し、`QuiltRenderer.LastCpuSubmissionTicks`をCPU submission時間として記録しました。これはGPU frame timeやpresent latencyではありません。

| GRD | Views | p50 CPU submission (ms) | p95 (ms) | GRD Renderers | Visible Instances | GC bytes/frame |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Off | 1 | 0.3592 | 0.4584 | 0 | 0 | 312 |
| On | 1 | 0.3174 | 0.3920 | 75 | 75 | 312 |
| Off | 8 | 2.7992 | 3.1957 | 0 | 0 | 1,656 |
| On | 8 | 2.4638 | 2.8579 | 75 | 600 | 1,656 |
| Off | 48 | 17.3311 | 19.6056 | 0 | 0 | 9,336 |
| On | 48 | 15.0732 | 16.6753 | 75 | 3,600 | 9,336 |

6条件のsummaryはすべて成功しています。今回の各条件は120 frame warmup後に1000 frameを3 run、合計3000 samplesを取得し、build GUID `e3ae196fd1af4b6583ee9b27ee28b557`で揃えています。48視点のCPU submission p50はGRD Onが15.0732 ms、Offが17.3311 msです。これは固定harnessの観測値であり、公式SDKやGPU実装の高速化を示す比較ではありません。GPU timingとCPU render-thread timingは欠測で、0を実測値として扱いません。GPU Resident Drawerは48 viewを1 drawへ変換せず、Bridgeのスレッド停止を修正する機能でもありません。

固定条件のGRD比較では、同じ75 mesh・128x96 tile・120 frame warmup・1000 frame x 3 run・Profiler instrumentation有効の条件で、VolumeStack cache変更前後を比較しました。

| GRD | Views | cache前p50 (ms) | cache前p95 (ms) | cache前GC/frame | cache後p50 (ms) | cache後p95 (ms) | cache後GC/frame |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Off | 1 | 0.5766 | 1.4776 | 32,620 | 0.3592 | 0.4584 | 312 |
| On | 1 | 0.5461 | 1.4107 | 32,620 | 0.3174 | 0.3920 | 312 |
| Off | 8 | 5.3800 | 6.1491 | 260,120 | 2.7992 | 3.1957 | 1,656 |
| On | 8 | 5.2666 | 6.0726 | 260,120 | 2.4638 | 2.8579 | 1,656 |
| Off | 48 | 31.7737 | 34.4383 | 1,560,120 | 17.3311 | 19.6056 | 9,336 |
| On | 48 | 30.5441 | 33.4241 | 1,560,120 | 15.0732 | 16.6753 | 9,336 |

cache変更後の測定では48 viewのGRD OnがOffに対してCPU submission p50で約13.0%低くなりました。cache変更前GRD Onからcache変更後の新GRD Onへの差は約50.7%です。これはGRDの差とVolumeStack cache変更の差を分けた観測で、公式SDKやGPU高速化の保証ではありません。測定PlayerはBridgeへ接続せず、別のDirect Lit 48 view表示Playerを両条件の背景負荷として同時に動作させています。

再現時は同一の機器、scene、Quilt、native views、quality、postprocess、compiler、graphics mode、Bridge、VSync、GPU、Unity、render pipelineを固定してください。性能測定中はPNG保存、trace、CPU readbackを無効にし、画質確認やログ取得は別実行に分けます。GRD単体の適用確認と、独自multiviewや再投影の比較条件は [gpu-resident-drawer.md](gpu-resident-drawer.md) と [multiview-rendering.md](multiview-rendering.md) を参照してください。

現在の実測sceneは約75 meshの代表 workloadです。別scene、mesh数、display、resolutionへ一般化できません。今回のcache変更後の測定はVolumeStack cache変更後の同条件で取得しています。異なるUnity/公式版との比較は条件差を明示した参考値に限り、直接のspeedup claimには使いません。

解像度のtradeoff例として、demo scale 0.25では1920x1080 Quilt、8x6、tile 240x180、profile 1.0では7680x4320、tile 960x720です。pixel数は16倍になり、RGBA32の7680x4320 Quiltは約126.56 MiBです。これはprofile例であり、全機種の固定値やfull-resolution実測結果ではありません。

生のJSONとplayer logは内部検証用として管理しています。表の数値は、記載した機器・scene・設定・harness条件に限定した測定結果です。公開APIや製品全体の性能保証、公式speed比較を示すものではありません。公式speed比較は未実施です。

