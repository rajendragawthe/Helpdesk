// Builds the document rendered inside the sandboxed email iframe. The iframe (no allow-scripts) and the CSP
// below are the security boundary; removing active elements here is defence in depth and keeps the layout sane.
const CSP = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:"

const BASE_STYLE = [
  'body{margin:0;padding:12px;font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;',
  'font-size:14px;line-height:1.5;color:#1f2937;background:#ffffff;overflow-wrap:anywhere}',
  'img,table{max-width:100%}img{height:auto}a{color:#2563eb}',
].join('')

// Elements that can load, run or navigate: dropped before the HTML is re-serialized.
const ACTIVE_ELEMENTS = 'script, noscript, iframe, frame, frameset, object, embed, applet, form, link, meta, base, template'

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
  parsed.querySelectorAll('a[href], area[href]').forEach((element) => {
    // eslint-disable-next-line no-control-regex
    const href = (element.getAttribute('href') ?? '').replace(/[\u0000-\u0020\u007f]/g, '').toLowerCase()
    if (/^(javascript|data|vbscript):/.test(href)) element.removeAttribute('href')
  })

  // The email's own CSS is kept, but every `<` is replaced by a CSS escape so that no markup can ever be
  // re-formed from the CSS text when it is serialized into our <style> block.
  const emailCss = styles.join('\n').replace(/</g, '\\3c ')

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
