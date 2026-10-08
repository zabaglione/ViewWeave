[English](THIRD_PARTY_NOTICES.md)

# Third-party notices

本リポジトリのViewWeave Core、URP、HDRP、Bridge adapterは、Looking Glass Factoryの公式Unity SDKとは独立して開発している非公式の実装です。公式SDKのsource code、Bridge binary、外部header、Unity Render Pipelineのforkは同梱しません。公式製品との提携や公式brandingの使用許諾を示しません。

Bridge adapterは、利用者が別途インストールしたBridge runtimeの公開ABIへ動的に接続します。runtime、header、libraryの再配布権、商用利用、リポジトリ公開への適合性はこの文書では判断しません。利用者は対象runtimeの契約を確認してください。

URP/HDRPはUnityの公開rendering/volume APIを利用します。Unity packageは依存関係として参照しますが、source codeはfork・同梱しません。Unity、Bridge、機器ソフトウェア、その他外部依存のライセンスを本リポジトリが付与するものではありません。

本リポジトリの独自コードはApache-2.0で公開します。Bridge、Unity、商標、第三者特許など外部権利は別条件であり、runtimeの再配布権・商用利用・リポジトリ公開への適合性は利用者が確認してください。

Bridge Native Function Referenceには、runtime libraryをコピー・配布できる場合がある旨の説明があります。ただし、これは一般的な再配布許諾の宣言ではありません。Looking GlassのTermsには個人利用・非商用利用を前提とする条件、配布や派生物に関する制限が記載されており、Bridge 2.6.3の個別契約へどの条件が適用されるかは本リポジトリでは確定していません。商用利用、公開リポジトリへの同梱、runtime・header・libraryの再配布は、公開前に対象vendor文書と個別契約を確認するrelease gateです。法律上の結論を示すものではありません。

本リポジトリは公式Bridge binary、header、source code、Unity packageのforkを同梱しません。利用者は必要なBridge runtimeを各自の契約条件に従って導入してください。独自コードのApache-2.0ライセンスは、Bridge runtimeやUnityの利用権を拡張しません。

Bridge 2.6.3のインストール版noticeは、Bridge本体をLooking Glass Factoryのproprietary softwareとして扱い、vendorのTermsを参照しています。同noticeに記載された同梱ライブラリには個別ライセンス条件があります。本リポジトリはBridge本体や同梱ライブラリの権利を付与せず、BridgeをApache-2.0として扱いません。

参照した一次資料は挙動や公開APIの調査用です。これらの参照は公式との提携、承認、完全互換性を意味しません。

- [Looking Glass Unity Plugin](https://lookingglassfactory.com/software/looking-glass-unity-plugin)
- [Looking Glass software documentation](https://lfdocs.lookingglassfactory.com/software/index)
- [Looking Glass Unity Plugin 4.0 alpha](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)
- [Bridge native function reference](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference)
- [Looking Glass terms and conditions](https://lookingglassfactory.com/terms-and-conditions)
- [Unity Custom Package Samples](https://docs.unity3d.com/6000.0/Documentation/Manual/cus-samples.html)
- [Unity UPM Git dependencies](https://docs.unity.com/en-us/engine/6000.0/manual/packages-list/upm-git)
