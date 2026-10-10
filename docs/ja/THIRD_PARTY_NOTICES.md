[English](../THIRD_PARTY_NOTICES.md)

# 第三者ライセンス表記

ViewWeaveはLooking Glass Factoryの公式Unity SDKとは独立して開発する非公式の実装です。公式との提携、承認、完全互換性、公式ブランドの使用許諾を示すものではありません。

## ViewWeaveの独自コード

独自コードは[Apache-2.0](../../LICENSE)です。Unity、Looking Glass Bridge、機器ソフトウェア、商標、第三者特許、その他の外部依存には別の条件があります。URP/HDRPはUnity packageを依存関係として参照し、そのソースをfork・同梱していません。

## Unity Native Plugin API

独自ネイティブshimのビルドには外部のUnity PluginAPI宣言を使います。packageにはコンパイル済みshimと[ネイティブプラグインの通知](../../Packages/com.zabaglione.lookingglass.viewweave.bridge/NOTICE.md)を含みますが、外部ヘッダや公式Unityプラグインの実装は同梱しません。

Unity Native Plugin API copyright © 2015 Unity Technologies ApS

公開ヘッダの`IUnityInterface.h`、`IUnityGraphics.h`、`IUnityGraphicsD3D12.h`には、Unityに依存するプロジェクト向けの**Unity Companion License**が指定されています。[Unity Companion License v1.4の全文](../../Packages/com.zabaglione.lookingglass.viewweave.bridge/ThirdPartyLicenses/Unity-Companion-License.txt)をソースBridge package、両パイプラインの配布物、今後生成するmacOS CI artifactへ同梱します。この条件は独自コードのApache-2.0とは別です。[Unityの公式ページ](https://unity.com/legal/licenses/unity-companion-license)も参照してください。

Copyright (c) 2016, Unity Technologies

固定したNativeRenderingPluginリポジトリの[MITライセンス全文](../../Packages/com.zabaglione.lookingglass.viewweave.bridge/ThirdPartyLicenses/Unity-NativeRenderingPlugin-MIT.txt)も同梱します。個別の優先する通知がない参照ファイル（`IUnityGraphicsMetal.h`、`IUnityGraphicsD3D11.h`など）のための通知であり、明示されたUnity Companion Licenseを置き換えません。公開参照元は[commit 522254181faf188efa8b50c3e3bf6fce720b26e4](https://github.com/Unity-Technologies/NativeRenderingPlugin/tree/522254181faf188efa8b50c3e3bf6fce720b26e4)です。

macOSの来歴には、その参照元とビルドに使用した3ヘッダのSHA-256を記録しています。Windowsの来歴はインストール版Unity 6000.6.4f1のPluginAPIを記録していますが、当時の個別ヘッダのハッシュと通知は保持されていません。これを固定した公開参照元と同一とは扱いません。同梱通知はUnityの外部権利を示すもので、Apache-2.0をそのヘッダへ拡張するものではありません。Windowsヘッダの厳密な来歴は未検証です。

## 別途導入するLooking Glass Bridge

ViewWeaveは別途インストールしたBridge runtimeの公開ABIへ動的に接続します。公式Bridgeのバイナリ、ヘッダ、ライブラリ、SDKソースはリポジトリやpackageへ同梱しません。確認したBridge 2.6.3のインストール版通知はBridgeをLooking Glass Factoryのproprietary softwareとして扱い、vendorのTermsを参照しています。runtimeに付属するライブラリにも個別ライセンスがあります。

利用者に適用される契約に従ってruntimeを導入・使用してください。ViewWeaveのApache-2.0はBridgeの商用利用・再配布権を付与しません。[Native Function Reference](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference)には再配布できる場合の説明がありますが、一般的な許諾ではありません。商用利用やvendorソフトウェアの同梱を行う場合は、[vendorのTerms](https://lookingglassfactory.com/terms-and-conditions)と個別契約を確認してください。本プロジェクトはすべてのBridge契約の条件を確定しておらず、法律上の結論を示すものではありません。

## API・挙動の参照資料

以下は調査に使用した一次資料であり、公式SDK実装のコピーではありません。

- [Looking Glass Unity Plugin](https://lookingglassfactory.com/software/looking-glass-unity-plugin)
- [Looking Glass software documentation](https://lfdocs.lookingglassfactory.com/software/index)
- [Looking Glass Unity Plugin 4.0 alpha](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)
- [Bridge native function reference](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference)
- [Unity Custom Package Samples](https://docs.unity3d.com/6000.0/Documentation/Manual/cus-samples.html)
- [Unity UPM Git dependencies](https://docs.unity.com/en-us/engine/6000.0/manual/packages-list/upm-git)
