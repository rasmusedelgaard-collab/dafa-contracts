// Client-side checks for uploaded files; the API enforces the same limits.

export const UPLOAD_EXTENSIONS = ['pdf', 'docx', 'doc', 'xlsx', 'xls', 'pptx', 'msg', 'eml', 'txt', 'png', 'jpg', 'jpeg'] as const;
export const UPLOAD_MAX_BYTES = 25 * 1024 * 1024;
/** For `<input type="file" accept>`. */
export const UPLOAD_ACCEPT = UPLOAD_EXTENSIONS.map((e) => `.${e}`).join(',');
export const UPLOAD_HINT = 'PDF, Word, Excel, PowerPoint, e-mail (.msg, .eml), text or image, up to 25 MB.';

/** Returns a message for the user when the file cannot be uploaded, otherwise undefined. */
export function uploadError(file: { name: string; size: number } | null | undefined): string | undefined {
  if (!file) return 'Choose a file';
  const ext = /\.([a-z0-9]+)$/i.exec(file.name)?.[1]?.toLowerCase();
  if (!ext || !(UPLOAD_EXTENSIONS as readonly string[]).includes(ext)) return `This file type cannot be uploaded. Use one of: ${UPLOAD_EXTENSIONS.join(', ')}`;
  if (file.size === 0) return 'The file is empty';
  if (file.size > UPLOAD_MAX_BYTES) return 'The file is larger than 25 MB';
  return undefined;
}

export const LOCKED_MESSAGE = 'Approved terms are locked. Register an amendment as a linked contract, or ask an administrator.';
