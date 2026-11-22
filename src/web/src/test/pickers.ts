import { screen, within } from '@testing-library/react';
import type { UserEvent } from '@testing-library/user-event';

/** Names offered by an open picker (the first line of each option). */
export function offered(): string[] {
  const list = screen.getByRole('listbox');
  return within(list)
    .queryAllByRole('option')
    .map((o) => o.querySelector('.truncate')?.textContent ?? o.textContent ?? '');
}

/** Opens a picker and lists everything it offers without typing. */
export async function openPicker(user: UserEvent, input: HTMLElement): Promise<string[]> {
  await user.click(input);
  return offered();
}

/** Types part of a name into a picker and clicks the matching option. */
export async function pick(user: UserEvent, input: HTMLElement, name: string): Promise<void> {
  await user.click(input);
  await user.type(input, name.slice(0, Math.min(5, name.length)));
  const list = await screen.findByRole('listbox');
  const option = within(list)
    .getAllByRole('option')
    .find((o) => (o.querySelector('.truncate')?.textContent ?? '') === name);
  if (!option) throw new Error(`"${name}" is not offered: ${offered().join(', ')}`);
  await user.click(option);
}
