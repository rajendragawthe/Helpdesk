// Builds the document rendered inside the sandboxed email iframe. The iframe (no allow-scripts) and the CSP
// below are the security boundary; removing active elements here is defence in depth and keeps the layout sane.
const CSP = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:"

const BASE_STYLE = [
  'body{margin:0;padding:12px;font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;',
  'font-size:14px;line-height:1.5;color:#1f2937;background:#ffffff;overflow-wrap:anywhere}',
  'img,table{max-width:100%}img{height:auto}a{color:#2563eb}',
].join('')

// Elements that can load, run or navigate: dropped before the HTML is re-serialized.
const ACTIVE_ELEMENTS = 'script, noscript, iframe, frame, frameset, object, embed, applet, form, link, meta, base, template, animate, set, animateMotion, animateTransform'

export function buildEmailDocument(html: string): string {
  // DOMParser documents are inert: nothing in `html` runs or loads while it is parsed here.
  const parsed = new DOMParser().parseFromString(html, 'text/html')

  // Only HTML-namespace <style> elements are kept. SVG/MathML <style> content is parsed differently (entities and
  // CDATA are decoded), so its text can contain markup-looking sequences; those elements are dropped entirely.
  const HTML_NS = 'http://www.w3.org/1999/xhtml'
  const styles = Array.from(parsed.querySelectorAll('style'))
    .filter((style) => style.namespaceURI === HTML_NS)
    .map((style) => style.textContent ?? '')
  parsed.querySelectorAll(`${ACTIVE_ELEMENTS}, style`).forEach((element) => element.remove())

  // Drop link targets that would run or embed content when clicked.
  // Covers href and xlink:href on every element (HTML <a>/<area>, SVG <a>/<use>, ...).
  parsed.querySelectorAll('*').forEach((element) => {
    for (const attr of Array.from(element.attributes)) {
      const name = attr.name.toLowerCase()
      if (name !== 'href' && !name.endsWith(':href')) continue
      // eslint-disable-next-line no-control-regex
      const value = attr.value.replace(/[\u0000-\u0020\u007f]/g, '').toLowerCase()
      if (/^(javascript|data|vbscript):/.test(value)) element.removeAttributeNode(attr)
    }
  })

  // The email's own CSS is kept. Outlook-style `<!-- ... -->` wrappers are removed first (they would otherwise
  // break the first rule once `<` is escaped); then every remaining `<` is replaced by a CSS escape so that no
  // markup can ever be re-formed from the CSS text when it is serialized into our <style> block.
  const emailCss = styles.join('\n').replace(/<!--|-->/g, '').replace(/</g, '\\3c ')

  return [
    '<!doctype html><html><head><meta charset="utf-8">',
    `<meta http-equiv="Content-Security-Policy" content="${CSP}">`,
    '<meta name="referrer" content="no-referrer">',
    '<base target="_blank">',
    `<style>${BASE_STYLE}</style>`,
    `<style>${emailCss}</style>`,
    '</head><body>',
    parsed.body.innerHTML,
    '</body></html>',
  ].join('')
}
