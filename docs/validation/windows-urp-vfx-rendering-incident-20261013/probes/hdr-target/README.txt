Phase A HDR target comparison artifacts.
Isolated Unity 6000.6.4f1 PlayMode probes on D3D12; source hash 2E083793F50E6FEAD435740C239EB6E74B1686307D9AAC1DF7C0F3299F956F96.
Green and Coin each ran one exact NUnit case. The normal reference target was linear ARGBHalf; the quilt target retained its observed UNorm sRGB format. PP was disabled by the probe. This is limited isolated-project evidence, not a product compatibility claim or device/Bridge test.
runner.json contains process/test gates and evidence hashes. nunit.xml is the exact test result. evidence/ contains paired rows, target descriptors, safe observations, float readbacks and diagnostic PNGs.
Full Unity Editor logs are intentionally excluded.
