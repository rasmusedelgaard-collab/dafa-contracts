// Client-side checks for uploaded files; the API enforces the same limits.

export const UPLOAD_EXTENSIONS = ['pdf', 'docx', 'doc', 'xlsx', 'xls', 'pptx', 'msg', 'eml', 'txt', 'png', 'jpg', 'jpeg'] as const;
export const UPLOAD_MAX_BYTES = 25 * 1024 * 1024;
/** For `<input type="file" accept>`. */
export const UPLOAD_ACCEPT = UPLOAD_EXTENSIONS.map((e) => `.${e}`).join(',');
export const UPLOAD_HINT = 'PDF, Word, Excel, PowerPoint, e-mail (.msg, .eml), text or image, up to 25 MB.';

/** New contract: the file the AI reads (`intake/extract`) can only be a PDF or a Word .docx file. */
export const INTAKE_EXTENSIONS = ['pdf', 'docx'] as const;
export const INTAKE_ACCEPT = INTAKE_EXTENSIONS.map((e) => `.${e}`).join(',');
export const INTAKE_TYPE_MESSAGE = 'Only PDF and Word (.docx) files can be read here. Save the file as PDF or .docx, or create the contract without a file and add this one under Documents';

/** As `uploadError`, for the New contract upload. */
export function intakeUploadError(file: { name: string; size: number } | null | undefined): string | undefined {
  if (!file) return 'Choose a file';
  const ext = /\.([a-z0-9]+)$/i.exec(file.name)?.[1]?.toLowerCase();
  if (!ext || !(INTAKE_EXTENSIONS as readonly string[]).includes(ext)) return INTAKE_TYPE_MESSAGE;
  return uploadError(file);
}

/** Returns a message for the user when the file cannot be uploaded, otherwise undefined. */
export function uploadError(file: { name: string; size: number } | null | undefined): string | undefined {
  if (!file) return 'Choose a file';
  const ext = /\.([a-z0-9]+)$/i.exec(file.name)?.[1]?.toLowerCase();
  if (!ext || !(UPLOAD_EXTENSIONS as readonly string[]).includes(ext)) return `This file type cannot be uploaded. Use one of: ${UPLOAD_EXTENSIONS.join(', ')}`;
  if (file.size === 0) return 'The file is empty';
  if (file.size > UPLOAD_MAX_BYTES) return 'The file is larger than 25 MB';
  return undefined;
}

/** Shown above Edit details when some fields are locked. Fields that are still empty can be filled in once. */
export const LOCKED_MESSAGE = 'Approved terms are locked. Register an amendment as a linked contract, or ask an administrator. Fields that are still empty can be filled in.';
