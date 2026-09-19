# ResoniteLinkセッション探索

preview.13からNuGet配布版でも利用可能。公式`YellowDogMan.ResoniteLink` 0.13.1に含まれる`LinkSessionListener`をadapter内で使用し、UnitySDKと同じセッション通知を受信する。依存バージョンの更新やポート総当たりは不要。

```powershell
resoloop discover --json
resoloop status --url auto --json
resoloop status --url auto --session 'orange World' --json
resoloop status --url auto --session 'S-...' --discovery-seconds 20 --json
```

`discover`はUDP 12512を既定12秒間listenし、`durationSeconds`、`count`、`sessions`を返す。各候補は`sessionId`、`sessionName`、`url`、`lastSeenUtc`を持つ。待機時間は`--discovery-seconds`で1～60秒。公式通知間隔は10秒なので、短い待機で0件でも起動していないとは限らない。期限終了時の候補を返し、最初の通知だけで接続先を決めない。閉鎖通知、更新、期限切れは公式listenerに従い、adapterでも不正portと古い通知を除外する。

`--url auto`は既存のWebSocket接続を使うcommand（status、doctor、inspect、apply、live capture、Flux deployなど）で共通に使える。探索結果から一意に選択してから接続し、以後そのURLを使用する。`--session`はIDまたは名前との大文字小文字を区別する完全一致で、`--url auto`と組み合わせる。同名候補が複数ならIDで選ぶ。探索だけでは接続可能性やworld内容を検証していないため、実際の接続・対象確認は引き続き必要。

環境変数`RESONITE_LINK_URL=auto`またはconfigの`"resoniteLinkUrl": "auto"`でも有効にできる。既存のCLI > environment > project > user優先順位を保ち、通常のURL指定が失敗しても別セッションへ切り替えない。探索結果を自動的にconfigへ保存しない。URL未設定時はエラーの案内から探索を選べる。

同梱スキルを使うAIは、有効な接続先が未設定ならユーザーにportを尋ねる前に自分で`discover`を実行する。1件なら追加確認なしで接続し、複数なら既にユーザーが選んだsessionを優先し、未選択なら名前とURLを示して選んでもらう。0件なら20秒で1回再試行し、なお未検出なら有効化またはURLを依頼する。選択したURLは後続commandへ明示して、毎回の再探索と接続先の切り替わりを避ける。これはAIの作業手順であり、URL未指定のCLI自体が自動接続する仕様への変更ではない。

| 状態 | 結果 |
| --- | --- |
| `discover`が0件 | 成功、空の一覧。待機時間やResoniteLinkの有効化を確認 |
| autoが0件／指定と一致しない | `RESONITE_DISCOVERY_NOT_FOUND` |
| autoで複数一致 | `RESONITE_DISCOVERY_AMBIGUOUS`、候補一覧付き |
| UDPをlistenできない | `RESONITE_DISCOVERY_UNAVAILABLE` |
| 通常URLと`--session`を併用 | `DISCOVERY_SELECTOR_REQUIRES_AUTO` |
| Ctrl+C／command deadline | listenerを破棄し、既存の`CANCELLED`／`COMMAND_TIMEOUT`を返す |

このPCのLANアドレスから来た通知も接続URLは`localhost`にする。実機ではLANアドレスへのWebSocket接続がHTTP 400になり、UnitySDKと同じlocalhost指定で接続できた。別PCからの通知は送信元アドレスと通知内の`linkPort`を組み合わせる（UDP送信元portを流用しない）。LAN越しの利用は通知到達と接続許可が必要で、リモート接続の実機検証は未実施。

探索の`sessionId`は通知に含まれるworldセッションIDで、`status`の`connectionId`とは異なる。sessionを再起動したら再探索する。以前の接続で観測したSlot/Componentのraw IDをそのまま再利用しない。

実装根拠:

- [公式LinkSessionListener（0.13.1の固定commit）](https://github.com/Yellow-Dog-Man/ResoniteLink/blob/067afad3d1977b3806cbad077f4e8a534f56f451/ResoniteLink/LinkSessionListener.cs)
- [公式通知モデル](https://github.com/Yellow-Dog-Man/ResoniteLink/blob/067afad3d1977b3806cbad077f4e8a534f56f451/ResoniteLink/Models/ResoniteLinkSession.cs)
- ユーザー指定のUnitySDK `Assets/ResoniteSDK/Editor/Managers/ResoniteLinkWindow.cs` のAutoDiscoveryとConnectPressed。参照のみでSDKは変更していない。

live回帰テストは`RESOLOOP_RUN_INTEGRATION=1`でopt-in。複数セッションがある場合は`RESOLOOP_DISCOVERY_SESSION`でIDまたは名前を明示して`SessionDiscoveryIntegrationTests`を実行する。通知受信とsession情報取得だけを行い、worldを変更しない。

2026-09-19の実機確認: Resonite 2026.9.18.82 / ResoniteLink 0.13.1.0の「orange World」を発見し、手動port指定なしで`ws://localhost:64696/`へ接続成功。command deadlineによる探索中断後にも、新しい探索・接続が成功した。
