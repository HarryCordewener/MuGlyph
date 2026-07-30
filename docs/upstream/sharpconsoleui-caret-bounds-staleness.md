# The text cursor stops following its control once a window has seen one mouse event

**Project:** SharpConsoleUI (`nickprotop/ConsoleEx`)
**Version examined:** `v2.5.14` (tag, commit `928331fb`). All `file:line` references below are against that tag.
**Affects:** caret placement and caret visibility for any `ILogicalCursorProvider` control in a window that has received at least one mouse event.

---

## 1. Symptom

A user of an application built on SharpConsoleUI reports that the terminal's text cursor drifts away from the line it is editing. In their words:

> "…going out of the terminal and then back in again to force a full DOM redraw, it corrects itself. But when I type a character, it does not."

Those two observations are the whole bug. Moving the pointer across the terminal is the only action that refreshes the data the caret is placed from. Typing invalidates and re-arranges the layout — the control is repainted at its new row — but the caret keeps the position it had when the pointer was last over the window.

In practice this shows up whenever a focused input control changes position without a mouse event: an input area that grows or shrinks with wrapped text, a status line appearing above it, a window resize, a scroll driven by the keyboard. The painted control moves; the caret does not.

---

## 2. Mechanism

### 2.1 The cache

`ControlBounds.ControlContentBounds` is a settable rectangle (`SharpConsoleUI/Layout/ControlBounds.cs:36`), initialised to `Rectangle.Empty` in the constructor (`:69`). `ControlBounds` instances live in `WindowLayoutManager._controlBounds`, a `Dictionary<IWindowControl, ControlBounds>` (`SharpConsoleUI/Layout/ControlBounds.cs:164`).

That dictionary is only ever read from or added to. `GetOrCreateControlBounds` (`:188`) inserts, `GetControlBounds` (`:201`) reads; there is no `Clear` or `Remove` on it anywhere in the repository. Entries therefore persist for the life of the window, and a stale rectangle stays stale until something rewrites it.

### 2.2 The single writer

`ControlContentBounds` has exactly one writer in the library: `WindowEventDispatcher.UpdateControlLayout()` (`SharpConsoleUI/Windows/WindowEventDispatcher.cs:578`), which copies each top-level control's `LayoutNode.AbsoluteBounds` into it at `:604-609`.

`UpdateControlLayout()` is `private` and has exactly one call site:

```csharp
// SharpConsoleUI/Windows/WindowEventDispatcher.cs:167-172
public bool ProcessMouseEvent(Events.MouseEventArgs args)
{
    lock (_window._lock)
    {
        // Ensure layout is current before processing mouse events
        UpdateControlLayout();
```

No render path, no relayout path, no resize path calls it. The cache is refreshed by mouse events and by nothing else.

(For completeness: `WindowLayoutManager.UpdateLayout(int availableWidth, int availableHeight)` at `SharpConsoleUI/Layout/ControlBounds.cs:179-183` is a public method whose entire body is two comment lines — "Don't clear existing bounds, just update them" — and which updates nothing. It has no callers anywhere in the repository, including tests, examples and benchmarks. It is presumably a leftover from the pre-DOM layout model.)

### 2.3 The two readers on the caret path

The per-frame caret decision is `WindowEventDispatcher.HasInteractiveContent(out Point cursorPosition)` (`SharpConsoleUI/Windows/WindowEventDispatcher.cs:918`). It asks two questions:

- **where** — `_window._layoutManager.TranslateLogicalCursorToWindow(control)` at `:932`, which delegates to `WindowLayoutManager.TranslateLogicalCursorToContent` (`SharpConsoleUI/Layout/ControlBounds.cs:325`);
- **whether** — `_window.IsCursorPositionVisible(cursorPosition, control)` at `:938` (`SharpConsoleUI/Window.State.cs:302`).

Both preferred the cache over the live layout node, falling through to the node only while the cache was still empty.

`TranslateLogicalCursorToContent` (`SharpConsoleUI/Layout/ControlBounds.cs:352-386`):

```csharp
// Get control's bounds (which are already absolute window-content coordinates from DOM)
var bounds = GetOrCreateControlBounds(control);
var contentBounds = bounds.ControlContentBounds;

// For nested controls, ControlContentBounds is never populated (only top-level controls get it).
// Fall back to the DOM node's AbsoluteBounds which tracks all controls including nested ones.
if (contentBounds.Width == 0 && contentBounds.Height == 0)
{
    var node = _window._renderer?.GetLayoutNode(control);
    ...
    var ab = node.AbsoluteBounds;
    contentBounds = new Rectangle(ab.X, ab.Y, ab.Width, ab.Height);
}
```

`IsCursorPositionVisible` (`SharpConsoleUI/Window.State.cs:304-324`) has the same shape, with `ResolveLaidOutNode` as its fallback at `:319`.

The fallback comment describes it as a nested-control accommodation, and for nested controls that is exactly what it is. But the guard is `cache is empty`, not `control is nested` — so for a **top-level** control the fallback is the path taken until the first mouse event, and is never taken again afterwards. The caret is read fresh from the layout node right up to the moment the pointer enters the window, and frozen from then on.

### 2.4 Why the mouse "fixes" it

Crossing the terminal with the pointer delivers a mouse event, which reaches `ProcessMouseEvent`, which calls `UpdateControlLayout()`, which re-copies the node bounds into the cache. The caret snaps to the right place — and immediately goes stale again. That matches the reporter's description precisely: leaving and re-entering the terminal corrects it; typing does not.

---

## 3. Reproduction

A focused `PromptControl` in a window, pushed down by a `MarkupControl` above it that grows a line at a time. Between each growth: mutate the label, `window.Invalidate(Invalidation.Relayout)`, render, then compare three numbers — the prompt's `LayoutNode.AbsoluteBounds.Y`, the cached `ControlContentBounds.Y`, and the caret Y reported by `Window.GetCursorContentPosition(prompt)`. Deliver exactly one mouse event partway through with `HeadlessConsoleDriver.SimulateMouseEvent` + `system.Input.ProcessInput()`.

Measured against `v2.5.14` (120x40 driver, 60x20 window):

```
=== phase 1: no mouse event has ever reached the window ===
initial                            nodeY=  1  cachedY=EMPTY  caretY=   1  visible=True
after relayout #1                  nodeY=  2  cachedY=EMPTY  caretY=   2  visible=True
after relayout #2                  nodeY=  3  cachedY=EMPTY  caretY=   3  visible=True
after relayout #3                  nodeY=  4  cachedY=EMPTY  caretY=   4  visible=True

=== delivering ONE mouse move over the window ===
after 1 mouse event                nodeY=  4  cachedY=    4  caretY=   4  visible=True

=== phase 2: same relayouts, mouse never moves again ===
after relayout #4                  nodeY=  5  cachedY=    4  caretY=   4  visible=True
after relayout #5                  nodeY=  6  cachedY=    4  caretY=   4  visible=True
after relayout #6                  nodeY=  7  cachedY=    4  caretY=   4  visible=True
```

The caret tracks the node exactly until the mouse event, then stops. After six relayouts the control is arranged at row 7 and the caret is still reported at row 4 — three rows adrift, and growing with every further relayout.

The natural home for this as a test is the **"Audit desync probes"** region in `SharpConsoleUI.Tests/Controls/ScrollablePanelLayoutContractTests.cs:404`, which is already labelled `DESYNC #1 measure-vs-paint, #4 cursor staleness`. This is a third member of that family: cursor staleness at the *window* level rather than inside a scroll panel. The existing `LogicalCursorPosition_ReflectsScrollOffset_AfterRender` (`:440`) is the closest sibling; the assertion here would be `caretY == nodeY` after a relayout that follows a mouse event, rather than a range check.

---

## 4. Why the existing tests do not see it

The cache is populated only by mouse events that reach `WindowEventDispatcher.ProcessMouseEvent`. A test that never delivers one to a window therefore exercises the *fresh-node* fallback for every caret assertion it makes, no matter how thorough it is.

That is the case for every cursor-focused test file in the suite — none of them contains a `SimulateMouseEvent` call:

- `SharpConsoleUI.Tests/Controls/ScrollablePanelCursorVisibilityTests.cs`
- `SharpConsoleUI.Tests/Controls/CollapsiblePanelCursorTests.cs`
- `SharpConsoleUI.Tests/Rendering/PortalCursorTests.cs`
- `SharpConsoleUI.Tests/Controls/ScrollablePanelLayoutContractTests.cs`

Corroborating evidence that the cache is normally empty under test: `ScrollRangeIntoViewMatrixTests` has to assign `ControlContentBounds` by hand to make `BringIntoFocus` deterministic, and says so — "the input `BringIntoFocus` reads — normally filled by `UpdateControlLayout` from the DOM node" (`SharpConsoleUI.Tests/Controls/ScrollRangeIntoViewMatrixTests.cs:421-422`, assignment at `:449-450`).

Two notes on this, since a plausible-sounding version of this argument is wrong:

- In production the subscription happens inside the blocking main loop: `ConsoleWindowSystem.Run()` (`SharpConsoleUI/ConsoleWindowSystem.cs:943`) calls `Input.RegisterEventHandlers(_keyPressedHandler)` at `:982`, and `InputCoordinator.RegisterEventHandlers` (`SharpConsoleUI/Input/InputCoordinator.cs:56-60`) is what wires `_consoleDriver.MouseEvent += HandleMouseEvent`.
- **This is not a "handlers are never registered under test" problem.** `SharpConsoleUI.Tests/Infrastructure/TestWindowSystemBuilder.cs:26-34` registers them explicitly, with the comment "normally this happens inside `Run()`", and a number of tests do drive real mouse events through the dispatcher (`MouseEventTests`, `CollapsiblePanelScrollablePanelMouseTests`, `ChatTranscriptRealThingTest`, …). The gap is narrower and more specific: no test both delivers a mouse event *and then* asserts a caret position across a subsequent relayout.

A downstream client's own caret suite — 60 assertions across three terminal sizes, both bar states, wrapped and unwrapped text, and five input heights — reported 60/60 passing while the bug was live in the running application, for this reason. That figure is reported to us rather than something we can reproduce here; the `v2.5.14` file-level facts above are all directly verifiable.

---

## 5. Suggested fix

Invert the precedence in both readers: resolve the node that actually positions the control and use its bounds; consult `ControlContentBounds` only when no node positions the control at all.

Nothing is added and nothing is recomputed more often — the node lookup already happens on the fallback path. A stale source simply stops outranking a live one. Because the node is precisely what `UpdateControlLayout` copies *from* (`SharpConsoleUI/Windows/WindowEventDispatcher.cs:602-609`), preferring it cannot report anything the cache would not have reported at its last refresh.

### 5.1 `WindowLayoutManager.TranslateLogicalCursorToContent` — `SharpConsoleUI/Layout/ControlBounds.cs`

```diff
@@ -349,41 +349,47 @@
  			if (logicalPosition == null)
  				return null;
 
-			// Get control's bounds (which are already absolute window-content coordinates from DOM)
-			var bounds = GetOrCreateControlBounds(control);
-			var contentBounds = bounds.ControlContentBounds;
+			// Resolve the LIVE layout node first. ControlContentBounds is a cache whose only writer is
+			// WindowEventDispatcher.UpdateControlLayout(), which runs from ProcessMouseEvent — so once
+			// populated it stops tracking relayouts until the pointer moves again. The node is what
+			// UpdateControlLayout copies FROM, so preferring it cannot report anything the cache would
+			// not have reported at its last refresh.
+			var node = _window._renderer?.GetLayoutNode(control);
 
-			// For nested controls, ControlContentBounds is never populated (only top-level controls get it).
-			// Fall back to the DOM node's AbsoluteBounds which tracks all controls including nested ones.
-			if (contentBounds.Width == 0 && contentBounds.Height == 0)
+			// If this control has no LayoutNode, it lives inside a self-painting container
+			// (e.g. ToolbarControl). Walk up through Container to find the nearest ancestor
+			// that has a LayoutNode and can provide a cursor position.
+			if (node == null)
  			{
-				var node = _window._renderer?.GetLayoutNode(control);
-
-				// If this control has no LayoutNode, it lives inside a self-painting container
-				// (e.g. ToolbarControl). Walk up through Container to find the nearest ancestor
-				// that has a LayoutNode and can provide a cursor position.
-				if (node == null)
+				var current = control.Container as Controls.IWindowControl;
+				while (current != null)
  				{
-					var current = control.Container as Controls.IWindowControl;
-					while (current != null)
+					node = _window._renderer?.GetLayoutNode(current);
+					if (node != null && current is Controls.ILogicalCursorProvider parentCursor)
  					{
-						node = _window._renderer?.GetLayoutNode(current);
-						if (node != null && current is Controls.ILogicalCursorProvider parentCursor)
-						{
-							// The parent's GetLogicalCursorPosition() already accumulates the
-							// child's offset within the parent, so use it instead.
-							logicalPosition = parentCursor.GetLogicalCursorPosition();
-							if (logicalPosition == null) return null;
-							break;
-						}
-						current = current.Container as Controls.IWindowControl;
+						// The parent's GetLogicalCursorPosition() already accumulates the
+						// child's offset within the parent, so use it instead.
+						logicalPosition = parentCursor.GetLogicalCursorPosition();
+						if (logicalPosition == null) return null;
+						break;
  					}
-					if (node == null) return null;
+					current = current.Container as Controls.IWindowControl;
  				}
+			}
 
+			Rectangle contentBounds;
+			if (node != null)
+			{
  				var ab = node.AbsoluteBounds;
  				contentBounds = new Rectangle(ab.X, ab.Y, ab.Width, ab.Height);
  			}
+			else
+			{
+				// No node positions this control at all — the cached bounds are the only source left.
+				contentBounds = GetOrCreateControlBounds(control).ControlContentBounds;
+				if (contentBounds.Width == 0 && contentBounds.Height == 0)
+					return null;
+			}
```

### 5.2 `Window.IsCursorPositionVisible` — `SharpConsoleUI/Window.State.cs`

Same inversion, and simpler because `ResolveLaidOutNode` (`SharpConsoleUI/Layout/ControlBounds.cs:288`) already encapsulates the walk:

```diff
@@ -301,27 +301,31 @@
  		internal bool IsCursorPositionVisible(Point cursorPosition, IWindowControl control)
  		{
-			// Get the control's bounds to understand its positioning
-			var bounds = _layoutManager.GetOrCreateControlBounds(control);
-			if (bounds == null) return false;
-			var controlBounds = bounds.ControlContentBounds;
+			// Resolve the node that actually positions this control on screen, and prefer it over the
+			// cached ControlContentBounds. That cache is only ever written by
+			// WindowEventDispatcher.UpdateControlLayout(), which runs from ProcessMouseEvent, so once
+			// populated it freezes at the layout the pointer last saw. A control inside a self-painting
+			// container (ScrollablePanelControl) has only an ORPHAN registered node (empty bounds, not
+			// reachable from the root); its real position is governed by its nearest root-reachable
+			// ancestor (the host), whose node ResolveLaidOutNode returns instead. The host has already
+			// validated the cursor against its own viewport, so checking the cursor against the host
+			// bounds here is the correct visibility gate.
+			var node = _layoutManager.ResolveLaidOutNode(control);
 
-			// For nested controls, ControlContentBounds is never populated (only top-level controls get it).
-			// Fall back to the DOM node's AbsoluteBounds which tracks all controls including nested ones.
-			if (controlBounds.Width == 0 && controlBounds.Height == 0)
+			Rectangle controlBounds;
+			if (node != null)
  			{
-				// (comment block moved above, unchanged in substance)
-				var node = _layoutManager.ResolveLaidOutNode(control);
-				if (node == null) return false;
-
  				var ab = node.AbsoluteBounds;
  				controlBounds = new Rectangle(ab.X, ab.Y, ab.Width, ab.Height);
  			}
+			else
+			{
+				// No node positions this control — the cached bounds are the only source left.
+				var bounds = _layoutManager.GetOrCreateControlBounds(control);
+				if (bounds == null) return false;
+				controlBounds = bounds.ControlContentBounds;
+				if (controlBounds.Width == 0 && controlBounds.Height == 0) return false;
+			}
```

### 5.3 Two things that are easy to get wrong

These are worth calling out because a straightforward-looking version of this change breaks both.

**(a) The node-resolution block in `TranslateLogicalCursorToContent` contains early `return null`s.** As written at `v2.5.14`, the ancestor walk ends with `if (node == null) return null;` (`SharpConsoleUI/Layout/ControlBounds.cs:381`) — because inside the old structure the cache was already known to be empty, so there was genuinely nothing left. If the cache lookup is simply moved *below* that block, the fallback becomes unreachable in exactly the case its own comment describes (a control with no node and no cursor-providing ancestor). The shape above avoids this by deleting that `return null` and letting control flow reach a `node == null` branch that reads the cache; the only remaining early return in the walk is the genuine one, where an ancestor cursor provider reports no cursor at all.

**(b) Preferring the node changes behaviour for a control with no node but a populated cache.** Previously that control got a position from the cache. A careless inversion returns `null` for it, and the caret disappears rather than moves. The shape above deliberately keeps the cache as a last resort so that case still resolves — but it is worth being explicit that this is a judgement call. If you would rather such a control report no cursor (on the grounds that a control the layout tree does not position has no defensible caret position), dropping the `else` branch is a one-line change; it is your call which contract you want.

### 5.4 Verification performed

Both edits were applied to a copy of the `v2.5.14` tree outside the repository and measured:

- **`SharpConsoleUI.Tests`: 5568 passed, 0 failed** — identical to the unpatched baseline on the same machine (5568/5568).
- The reproduction above, re-run against the patched library, gives `caretY = 5, 6, 7` for relayouts #4–#6 — tracking `nodeY` again while `cachedY` stays frozen at 4, which is the intended outcome: the cache is still stale, it just no longer wins.

---

## 6. Related readers, not fixed by the above

Mentioned for completeness rather than as part of the proposed change; each is a separate judgement.

- **`WindowEventDispatcher.BringIntoFocus`** (`SharpConsoleUI/Windows/WindowEventDispatcher.cs:980`) reads `ControlContentBounds` at `:986-987` with **no node fallback at all**. Before the first mouse event it therefore reads `Rectangle.Empty` (`contentTop = 0`, `contentHeight = 0`); afterwards it reads whatever the pointer last saw. This is the same staleness plus a degenerate initial case.

- **`Window.ScrollToControl`** (`SharpConsoleUI/Window.State.cs:242`) reads the cache at `:252-255`, also with no fallback. Its preceding comment — "CRITICAL: Force layout update to get fresh widget positions" followed by `Invalidate(Invalidation.Relayout)` at `:250` — describes an intent the call cannot fulfil, because `Invalidate` does not write this cache; only `UpdateControlLayout` does.

- **`Window.GetVisibleHeightForControl`** (`SharpConsoleUI/Window.Rendering.cs:115`) reads `ControlContentBounds.Height` at `:123-124` on its sticky-control branch, with no fallback.

- **`ControlBounds.WindowContentToControl` / `WindowToControl`** (`SharpConsoleUI/Layout/ControlBounds.cs:90`, `:113`) read the same field, but in practice are **not** affected: their only live caller is `WindowEventDispatcher.GetControlRelativePosition` (`:543-561`), which is reached only from inside `ProcessMouseEvent` — i.e. immediately after `UpdateControlLayout()` has just refreshed the cache in the same lock. This one is fine as it stands.

- **`WindowLayoutManager.GetControlAtWindowPosition`** (`SharpConsoleUI/Layout/ControlBounds.cs:399`) reads the cache via `WindowToControl` and has no callers anywhere in the repository. Dead as far as the current tree is concerned.

---

## 7. Offer

Happy to open this as a PR against `master` instead of leaving it as an issue — the change is the two hunks in §5 plus a probe test in the "Audit desync probes" region — if that suits you better. Equally happy to leave it here if you would rather take a different approach to the precedence question in §5.3(b).
