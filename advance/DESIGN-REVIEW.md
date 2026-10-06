# Desktop widget design review

Implemented in both independently deployable editions. The application is WPF, not a website: resource brushes replace CSS variables, FlowDirection replaces CSS direction, and native UI Automation names and menu/dialog behavior replace ARIA roles. Segoe UI provides Hebrew support without downloading a font.

## Ten priorities and chosen fixes

1. **Unclear dismissal:** retain the header close button while expanded; show the launcher only after collapsing.
2. **Overflowing assistant editor:** constrain the owned modal to the panel dimensions and dim the owner. Keep Escape and focus restoration.
3. **Unreadable text:** use at least 12-point labels, stronger secondary text, a wrapped status badge, and distinct border/text colors.
4. **Crowded header:** keep the existing new-conversation action with an explicit tooltip; assistant creation remains in the assistant menu.
5. **Ambiguous credentials:** keep the saved key locked and masked, identify its last four characters, and expose labeled 44-point view/copy/edit actions. Editing can still be cancelled.
6. **Unclear source paths:** provide a path example, folder browser, labeled test/remove actions, readable validation results, and consistent assistant terminology.
7. **Accidental history deletion:** use neutral delete icons, red hover/focus, and inline Yes/Cancel confirmation. Escape cancels confirmation.
8. **Confusing menus:** separate headings/actions with dividers, highlight the selected assistant, use a dimmed backdrop, and restore focus on dismissal. Native menus provide arrow-key navigation and Escape/outside dismissal.
9. **Empty chat and composer:** preserve the named welcome message, offer up to three frequent questions with requested examples as fallbacks, disable blank sends, retain the left-pointing send arrow and Enter/Shift+Enter behavior.
10. **Inconsistent states:** share palette resources, retain the original white-and-blue palette regardless of Windows theme, use short motion respecting Windows animation preference, show connection-test progress, and display document-refresh age.

## Hebrew copy changes

- `שם הצ׳אט`, `יצירת צ׳אט`, `מחיקת סוג הצ׳אט` → `שם העוזר`, `יצירת עוזר`, `מחיקת העוזר`: use one term for assistant configuration.
- History heading → `ממשיכים את השיחה`: avoid repeating the tab title.
- Recent dates use `היום` / `אתמול`; older dates retain day/month/year.
- Saved credentials show `מפתח שמור · מסתיים ב־…`; connection success says `החיבור תקין`.
- Shortened the no-path helper and added a prompt example, folder-browser label, inline delete confirmation, and an accurate credential-storage note.
- Removed the small `עברית או English` composer hint.

The plus button still means **new conversation**, not new assistant. Relabeling it as assistant creation would change existing functionality.

## Validation and limits

- Local and managed desktop builds/publishes succeeded.
- Existing regression suite: 49 knowledge tests and 6 server tests passed.
- Local executable launched successfully and Windows reported it responsive.
- Native accessibility inspection verified one visible close button, the welcome label, three suggested questions, and disabled blank send.
- Screenshot capture failed twice (`FrameArrived timed out` / `window capture timed out`). Input automation then reported `coordinate input geometry is unavailable`. Consequently, six-screen visual QA, dark-theme visual QA, narrow-screen layout, and end-to-end keyboard interaction still require manual review. Compilation and the existing tests are not proof of WCAG compliance.
- This change does not modify retrieval, endpoints, credentials, AD authorization, or the managed web administration interface.

## Manual release check

Open chat, assistant menu, assistant editor, history, options, and AI settings. Check narrow desktop scaling, white panels even when Windows uses dark mode, keyboard-only navigation, Escape/focus return, long paths and assistant names. Confirm deletion only on Yes; confirm cancel leaves the conversation intact. Test an empty send, Enter/Shift+Enter, source-folder browsing, API-edit cancellation, and successful/failed connection indicators with a test key.

## Menu regression fix
The global menu item-container style incorrectly applied a MenuItem style to Separator controls. It is now scoped by control type, so both menus can open safely. A WPF smoke executable reproduces the original failure and verifies both menus opening/closing, history open/delete/cancel, assistant path add/remove/create, model dropdown, API edit/cancel, and the fixed white surface. Run dotnet run --project development/tests/Chat.Smoke/Chat.Smoke.csproj from this edition. It uses an isolated temporary dummy credential and makes no AI requests.
