import type { MailboxDefaultsDto } from "../../api/types";

/** Mirrors the server: a company domain also covers its subdomains; a full address matches only itself. */
export function isCompanyAddress(email: string, defaults: MailboxDefaultsDto | null): boolean {
  if (!defaults) return true;
  const address = email.trim().toLowerCase();
  const domain = address.slice(address.lastIndexOf("@") + 1);
  return defaults.companyDomains.some((entry) =>
    entry.includes("@") ? entry === address : domain === entry || domain.endsWith(`.${entry}`));
}
