// Tiny file generators for the mock: a valid single-page PDF and an RTF document.

function pdfEscape(text: string): string {
  let out = '';
  for (const ch of text.replace(/[–—]/g, '-').replace(/[’‘]/g, "'").replace(/[“”]/g, '"')) {
    const code = ch.charCodeAt(0);
    if (ch === '\\' || ch === '(' || ch === ')') out += `\\${ch}`;
    else if (code >= 32 && code < 127) out += ch;
    else if (code >= 160 && code <= 255) out += `\\${code.toString(8).padStart(3, '0')}`;
    else out += '?';
  }
  return out;
}

function wrap(line: string, width = 92): string[] {
  if (line.length <= width) return [line];
  const out: string[] = [];
  let current = '';
  for (const word of line.split(' ')) {
    if ((current + ' ' + word).trim().length > width) {
      out.push(current);
      current = word;
    } else {
      current = (current + ' ' + word).trim();
    }
  }
  if (current) out.push(current);
  return out;
}

/** One A4 page of Helvetica text. The first line is set as a heading. */
export function makePdf(lines: string[]): Blob {
  const wrapped = lines.flatMap((l) => (l === '' ? [''] : wrap(l))).slice(0, 52);
  const content = [
    'BT',
    '/F1 16 Tf',
    '56 780 Td',
    `(${pdfEscape(wrapped[0] ?? '')}) Tj`,
    '/F1 10.5 Tf',
    '0 -26 Td',
    '14 TL',
    ...wrapped.slice(1).map((l) => `(${pdfEscape(l)}) Tj T*`),
    'ET',
  ].join('\n');
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>',
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
  ];
  let pdf = '%PDF-1.4\n';
  const offsets: number[] = [];
  objects.forEach((body, i) => {
    offsets.push(pdf.length);
    pdf += `${i + 1} 0 obj\n${body}\nendobj\n`;
  });
  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  pdf += offsets.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return new Blob([pdf], { type: 'application/pdf' });
}

function rtfEscape(text: string): string {
  let out = '';
  for (const ch of text) {
    const code = ch.charCodeAt(0);
    if (ch === '\\' || ch === '{' || ch === '}') out += `\\${ch}`;
    else if (code < 128) out += ch;
    else out += `\\u${code}?`;
  }
  return out;
}

/** A Word-readable RTF document; the mock's stand-in for the .docx the real API generates. */
export function makeRtf(text: string): Blob {
  const paragraphs = text.split('\n').map((l, i) => `${i === 0 ? '\\b\\fs32 ' : '\\b0\\fs22 '}${rtfEscape(l)}\\par`);
  const rtf = `{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Arial;}}\\f0\n${paragraphs.join('\n')}\n}`;
  return new Blob([rtf], { type: 'application/rtf' });
}
