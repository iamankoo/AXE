namespace AxeV2.Browser;

/// <summary>
/// Keeps the standard arrow cursor over web content (locked AXE v2 UI requirement).
/// <para>
/// Chromium draws the page cursor in its own process, so AXE cannot set it from outside;
/// instead every document and frame gets a stylesheet, once at document creation, that
/// forces <c>cursor: default</c>. It only changes how the pointer looks — hit-testing,
/// clicking, typing, selection, scrolling and dragging are unaffected — and it never reads
/// or reports anything about the page.
/// </para>
/// <list type="bullet">
/// <item>The rule lives in a cascade layer declared before any site CSS: in the cascade,
/// <c>!important</c> declarations in an earlier layer beat later layers and unlayered
/// <c>!important</c> rules, whatever their specificity.</item>
/// <item>Shadow roots (web components) get the same sheet, since document styles do not
/// reach into them.</item>
/// <item>Inline <c>style="cursor: … !important"</c>, the only thing that outranks a layered
/// important rule, is rewritten when it appears (parsed, inserted or changed later).</item>
/// </list>
/// </summary>
public static class CursorLock
{
    public const string Css =
        "@layer axe-cursor-lock{*,*::before,*::after,*::marker,*::placeholder,*::selection," +
        "*::-webkit-scrollbar,*::-webkit-scrollbar-thumb,*::-webkit-resizer{cursor:default!important}}";

    public const string Script = """
        (() => {
          'use strict';
          if (window.__axeCursorLock) return;
          Object.defineProperty(window, '__axeCursorLock', { value: true });
          const css = "@@CSS@@";

          // 1. A <style> placed first in the document declares the layer before any site CSS.
          const place = () => {
            const root = document.documentElement;
            if (!root) return false;
            const style = document.createElement('style');
            style.textContent = css;
            style.setAttribute('data-axe', '');
            root.insertBefore(style, root.firstChild);
            return true;
          };
          if (!place()) {
            const wait = new MutationObserver(() => { if (place()) wait.disconnect(); });
            wait.observe(document, { childList: true });
          }

          // 2. Shadow roots: document styles don't reach inside, so adopt the same sheet there.
          let sheet = null;
          try { sheet = new CSSStyleSheet(); sheet.replaceSync(css); } catch (e) { sheet = null; }
          if (sheet) {
            const attach = Element.prototype.attachShadow;
            Element.prototype.attachShadow = function (init) {
              const root = attach.call(this, init);
              try { root.adoptedStyleSheets = [sheet, ...root.adoptedStyleSheets]; } catch (e) { }
              return root;
            };
          }

          // 3. Inline !important cursors are the only declarations that outrank the layer.
          const fix = (el) => {
            const s = el && el.style;
            if (s && s.getPropertyPriority('cursor') === 'important' && s.getPropertyValue('cursor') !== 'default') {
              s.setProperty('cursor', 'default', 'important');
            }
          };
          // Elements the parser creates already carry their style attribute (no attribute
          // mutation), so inserted elements are checked too; only those with a cursor rule.
          const sweep = (node) => {
            if (node.nodeType !== 1) return;
            fix(node);
            if (node.firstElementChild) node.querySelectorAll('[style*="cursor"]').forEach(fix);
          };
          new MutationObserver((records) => {
            for (const r of records) {
              if (r.type === 'attributes') fix(r.target);
              else for (const n of r.addedNodes) sweep(n);
            }
          }).observe(document, { attributes: true, attributeFilter: ['style'], childList: true, subtree: true });
        })();
        """;

    /// <summary>The injected script with the stylesheet embedded.</summary>
    public static string DocumentScript { get; } = Script.Replace("@@CSS@@", Css);
}
