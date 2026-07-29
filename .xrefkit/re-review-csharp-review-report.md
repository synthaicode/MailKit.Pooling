# C#コードレビュー再レビュー報告

## Report

### Status

要確認 (`needs-review`)

### Result

前回レビューの `CR-001`〜`CR-003` を修正後のソースとテスト結果で再確認した。
3件の実装修正は有効と判定し、各Findingを `pass-after-fix` とした。
一方、Activity / correlation IDの責任境界（`TR-001`）は今回の修正対象外であり、再レビューでも要確認としてhandoffを維持する。

### Evidence

- 対象: `src/MailKit.Pooling` と関連テスト
- CR-001: `SmtpPool`の切断処理に1秒の期限を設け、期限後も`DisposeAsync`を実行。遅延切断の回帰テストで`DisposeCalls=1`を確認
- CR-002: `pendingConnectionDisposals`を`MaxPoolSize`・snapshot・作成判定に含め、破棄完了まで容量枠を予約。遅延破棄中の新規作成抑制テストが成功
- CR-003: connect/auth/send timeout、reconnect cooldown、最大cooldownの登録時検証を追加し、登録時異常値テストが成功
- Build: 警告0、エラー0
- Unit: net8.0 / net10.0 各73 passed
- Component: net8.0 / net10.0 各10 passed
- Docker統合: net8.0 / net10.0 各8 passed
- Docker有効ストレス: net8.0 / net10.0 各9 passed

### Open Items

- `TR-001`: Activity / correlation IDの責任境界はコードだけでは確定できない
- 前回レビューの元レポートは履歴として保持し、本レポートを修正後の判定結果とする

### Handoff

- `TR-001`を`qa_gate_review`または設計担当へ継続引き渡し
- `CR-001`〜`CR-003`は修正証拠により実装修正側では完了

## Check Item Matrix

| Category | 説明 | Status | Evidence / Reason | 詳細 |
|---|---|---|---|---|
| Attribute activation/precondition | [属性の有効化条件](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/skills/csharp_review/SKILL.md#attribute-activation-preconditions-rule) | not_applicable | 対象ソースに該当するカスタム属性の実行時消費構造なし | — |
| Resource efficiency | [リソース効率](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/knowledge/csharp/100_csharp_review_spec.md#resource-efficiency-checks) | pass-after-fix | 破棄中接続を容量計算から除外せず、期限後にDisposeへ到達 | [CR-001](#cr-001), [CR-002](#cr-002) |
| Operational resilience | [運用耐性](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/knowledge/source_analysis/100_common_source_analysis_criteria.md#operational-hazard-taxonomy) | pass-after-fix | 遅延切断時の資源保持と再作成競合を回帰テストで確認 | [CR-001](#cr-001), [CR-002](#cr-002) |
| Synchronization | [同期・並行性](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/knowledge/csharp/100_csharp_review_spec.md#synchronization-checks) | pass | lock下の状態更新と破棄容量解放通知を確認 | — |
| Required business input integrity | [必須入力の完全性](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/knowledge/source_analysis/100_common_source_analysis_criteria.md#required-input-integrity-review) | not_applicable | 対象範囲に業務判定入力なし | — |
| Support lifecycle | [サポートライフサイクル](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/skills/csharp_review/SKILL.md#support-lifecycle-checks) | pass | net8.0 / net10.0を確認 | — |
| Error handling | [例外処理](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/knowledge/csharp/100_csharp_review_spec.md#error-handling-and-exception-path-checks) | pass-after-fix | Disconnect timeout・例外時もDispose経路を維持 | [CR-001](#cr-001) |
| Time and culture | [時刻・カルチャー](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/knowledge/csharp/100_csharp_review_spec.md#time-and-culture-checks) | pass | DateTimeOffset / UtcNowを使用 | — |
| State and determinism | [状態・決定性](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/knowledge/csharp/100_csharp_review_spec.md#state-and-determinism-boundary-checks) | pass-after-fix | pending disposal状態を容量状態に含める | [CR-002](#cr-002) |
| Uncertainty/escalation | [不確実性・エスカレーション](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/knowledge/csharp/100_csharp_review_spec.md#uncertainty-and-escalation-path-checks) | pass | 送信結果分類とUnknownAfterDataの境界を維持 | — |
| Contract/schema resilience | [契約・スキーマ耐性](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/knowledge/csharp/100_csharp_review_spec.md#contract-and-schema-resilience-checks) | not_applicable | 外部シリアライズDTO境界なし | — |
| Traceability/context propagation | [トレーサビリティ・コンテキスト伝播](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/knowledge/csharp/100_csharp_review_spec.md#traceability-and-context-propagation-checks) | needs_confirmation | Activity / correlation IDの責任範囲は設計確認が必要 | [TR-001](#tr-001) |
| Roslyn baseline | [Roslynベースライン](https://github.com/synthaicode/XRefKit/blob/agent/public-evaluation-corpora/skills/csharp_review/SKILL.md#quality-gate) | pass | build: 0 warnings, 0 errors | — |

## Findings

### CR-001 — 修正確認: 接続破棄の期限後にDisposeへ到達する

- Severity: `major`（修正前）
- Re-review status: `pass-after-fix`
- Evidence: `SmtpPool.DisposeConnectionAsync`がDisconnectを期限付きで待機し、タイムアウト後もDispose経路を実行する。`SmtpSenderTests`の遅延cleanup回帰テストが成功
- Residual risk: 基底アダプターのDispose自体が停止する場合は、アダプター契約または別途設計確認が必要

### CR-002 — 修正確認: 破棄中接続をプール容量に含める

- Severity: `major`（修正前）
- Re-review status: `pass-after-fix`
- Evidence: `pendingConnectionDisposals`をlive connection countへ加算し、破棄完了時にfinallyで解放する。遅延破棄中はreplacementを作成しない回帰テストが成功

### CR-003 — 修正確認: 設定値を登録時に検証する

- Severity: `major`（修正前）
- Re-review status: `pass-after-fix`
- Evidence: ConnectTimeout、AuthenticateTimeout、SmtpSendTimeout、ReconnectCooldown、MaxReconnectCooldownをvalidatorで検証。DI登録時の異常値テストが成功

## Handoff Items

### TR-001 — Activity/correlation IDの責任境界

送信・再試行・cleanup・メトリクスを同一操作として結び付ける責任がライブラリ側か利用者側かは、今回の実装修正とテストだけでは確定できない。`qa_gate_review`または設計担当で確認する。

## Gate Verdict

```text
verdict: needs-review
reason: CR-001〜CR-003は修正証拠によりpass-after-fixだが、TR-001の責任境界が未確定
evidence: re-review-csharp-review-report.md; implementation-flow-run.md; build/test evidence
downgrade_reason: traceability/context propagationの責任範囲が設計確認待ち
required_followup: qa_gate_review または設計担当でTR-001を確認
```
