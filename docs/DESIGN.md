# Design Context

## Target Audience

Windows 上同時使用 Codex 與 Claude Code 的開發者與 AI 高頻使用者。他們在工作期間多次短暫開啟此系統匣面板，不會長時間停留。

## Primary Use Cases

- 一眼比較 Codex 與 Claude Code 的已用、剩餘與重設時間。
- 確認資料來源與新鮮度，並手動更新。
- 在接近限額前切換模型或安排工作。

## Brand Personality

原生、安靜、精準。介面應像 Windows 11 內建的專業狀態面板，保留 Codex 綠與 Claude 橘作為稀少且有意義的品牌訊號。

## Design Principles

- 資料優先：主要數字與重設時間必須先於裝飾。
- 短暫互動：針對 2–10 秒的開啟使用情境。
- 動效有語意：只用於開啟、更新與數值變化；不使用常駐流光、呼吸或彈跳。
- 原生性：優先採用官方品牌圖示、Segoe UI Variable 與 Windows Fluent 圖示。
- 無障礙：保留鍵盤導覽與焦點，尊重 Windows 減少動畫與高對比設定。

## Technical Constraints

- Windows WinForms / .NET Framework 4.8，不引入外部 UI 框架。
- 系統匣無邊框面板，支援 Per-Monitor V2 與 200% DPI。
- 動畫使用單一 Timer，閒置時必須停止，且不可影響背景資料更新。
