# ViewWeave HDRP

ViewWeave is an independently developed, unofficial Unity SDK for Looking Glass displays and is not an official Looking Glass Factory product.

正式対象はUnity 6.6系のHDRP（SRP 17.6）です。検証環境はUnity 6000.6.4f1、Windows、D3D11です。利用者向けの性能保証や光学品質保証はありません。

独自コードのライセンスは[Apache-2.0](LICENSE.md)です。Unity、Bridge、商標、第三者権利は別条件です。

実機表示にはWindowsのLooking Glass Bridge runtimeとD3D11 Playerが必要です。利用者はリポジトリの`Distribution/hdrp`をGit URLから導入し、Package Managerの`Windows HDRP Lit` sampleをImportして`GameObject > ViewWeave > Bridge Rig`から始めてください。Samplesは自動的にAssetsへimportされません。このsource packageはリポジトリ開発・テスト用です。

Bridge Rigが作成する専用Camera、`QuiltRenderer`、`HdrpQuiltCamera`、Bridge output、HDRP adapterを使用します。`QuiltRenderer.GuardBehaviour`へHDRP adapterを指定すると、各viewのBegin/Disposeを自動で利用します。利用者が外側で`PrepareForQuilt`を呼ぶ必要はありません。

Linear color spaceと固定Exposureが必要です。LitとBloomのsmoke確認済みです。VolumeStackはvalidation用であり、render historyの独立は保証しません。TAAは描画中だけ無効化し終了時に復元します。Motion Blur、SSR、SSGI、volumetric fog、cloud、固定以外のExposureは拒否します。

macOSの実機検証は未実施です。
