# Google Tasks Desktop Widget

Windows 11 x64 向けの Google Tasks ウィジェットです。WPF と .NET 10 を使い、Explorer の WorkerW に接続してデスクトップへ表示します。WorkerW を利用できない場合は通常ウィンドウの最背面へ配置します。

## 初回設定

1. Google Cloud Console で Google Tasks API を有効にします。
2. OAuth クライアントを「デスクトップ アプリ」として作成します。
3. アプリを起動し、ウィジェットの案内に従って Desktop 用 OAuth JSON を選択するか、Client ID を入力します。Client Secret が必要な OAuth クライアントでは Secret も入力できます。
4. OAuth 情報を保存し、「Google Tasks に接続」を選びます。

Desktop 用 JSON を選ぶと `installed.client_id` と `installed.client_secret` を読み込みます。手入力では Client ID 単独でも設定できます。Google が Client Secret を要求した場合はウィジェットに案内が表示されるので、JSON を読み込むか Secret を入力してください。

Client ID は EXE と同じ場所の `Data\settings.json` に保存されます。Client Secret は Windows DPAPI の CurrentUser scope で暗号化し、`Data\client-secret.dat` に保存します。設定ファイル、ログ、タスクキャッシュには平文の Secret を保存しません。OAuth 情報を変更すると、既存の refresh token と task cache を削除して再認証を求めます。

この配布 EXE は汎用ビルドです。OAuth Client ID や Secret は EXE に含めません。Google の token endpoint が `client_secret` を要求する Desktop OAuth クライアントにも対応しています。Installed app の Secret はアプリだけが知る秘密としては扱えないため、EXE に埋め込まず、Windows DPAPI でユーザー単位に暗号化して保存します。

## トラブルシューティング

- ウィジェットに「Google Tasks API を有効にしてください」と表示された場合は、[Google Cloud Console の API ライブラリ](https://console.cloud.google.com/apis/library/tasks.googleapis.com)で Google Tasks API を有効にしてください。OAuth クライアント ID を作成したものと同じ Google Cloud プロジェクトで有効にしてから、ウィジェットのメニューで「今すぐ更新」を選びます。
- 「Google Tasks の権限が不足しています」と表示された場合は、メニューから Google に再接続し、タスクへのアクセスを許可してください。
- 組織ポリシーによる制限が表示された場合は、Google Workspace 管理者に Google Tasks API の利用可否を確認してください。

## ビルドと配布

```powershell
dotnet publish .\src\GoogleTasksDesktopWidget\GoogleTasksDesktopWidget.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false `
  -o .\artifacts\release
```

EXE に OAuth Client ID は埋め込みません。配布時は publish 出力の `GoogleTasksDesktopWidget.exe` のみを渡します。.NET Runtime の追加インストール、管理者権限、インストーラーは不要です。初回起動時に EXE の隣へ `Data` フォルダを作ります。書き込み可能なフォルダに EXE を置いてください。

## ポータブルデータと旧版からの移行

設定、暗号化した認証情報、暗号化したキャッシュ、ログはすべて EXE と同じフォルダの `Data` に保存します。別の場所へ移すときは、EXE と `Data` を一緒に移してください。新しい PC または Windows ユーザーでは DPAPI で保護された認証情報を通常は復号できないため、Google に再認証してください。Client ID は設定ファイルに残ります。Desktop OAuth クライアントが Client Secret を要求する場合は、移動先で OAuth JSON を再度読み込むか Secret を再入力してください。

旧版の `%LocalAppData%\GoogleTasksDesktopWidget` があれば、初回起動時にファイルを `Data` へコピーし、コピー内容を照合します。移行先の既存ファイルは上書きせず、旧データも削除しません。移行済みマーカーで再起動時の重複移行を防ぎます。旧版で自動起動が有効だった場合は設定を引き継ぎます。新規利用では自動起動はオフです。EXE を移した後に自動起動を使う場合は、移動先の EXE を一度起動して登録パスを更新してください。

## 開発ビルドとテスト

```powershell
dotnet build .\GoogleTasksDesktopWidget.sln -c Debug
dotnet run --project .\tests\GoogleTasksDesktopWidget.CoreTests\GoogleTasksDesktopWidget.CoreTests.csproj -c Release
```

テストには Windows DPAPI の保存・読み込み確認が含まれます。OAuth と Google Tasks API の実アカウント連携は、利用者の Windows 環境で確認します。

## 操作

- 行のチェックでタスクを完了します。
- タスク行をクリックすると、その場でタイトル・詳細・期限を編集できます。日付は「今日」「明日」からも設定できます。
- 上部の「＋」からタスクを追加できます。
- 「完了」の開閉ボタンで完了済みタスクを表示できます。完了タスクには取り消し線と完了日を表示します。
- タスクにマウスを重ねるとオプションが表示されます。メニューから期限の設定、サブタスクの追加、削除、別リストへの移動ができます。「新しいリスト」はタスクメニューと上部のリスト選択メニューから作成でき、作成後はそのリストに切り替わります。
- 上部の点々メニューから並び替え、更新、ロック、自動起動、テーマ、OAuth Client ID などを設定できます。並び替えは指定順、期限、タイトルに対応しています。
- 上部の点々をドラッグして位置を動かします。位置はモニター名と DIP オフセットで保存します。

Google Tasks API が対応していない機能は、このウィジェットの画面に表示しません。

OAuth 以外の操作でブラウザーは開きません。ログは `Data\logs` に保存し、トークン、タスク名、メモ、OAuth response body は記録しません。OAuth token endpoint の失敗時は、切り分け用に stage、HTTP status、エラーコードのみ記録します。
