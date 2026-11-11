// The account rules the app checks too (AccountRules in src/GymMembership.Core/Services/Services.cs). Kept free of imports so the
// local tests (supabase/tests/local/edge-rules.mjs) load this exact file with Node.

/** D5: 8 to 64 characters with a letter and a number, counted as a to z and 0 to 9 like Supabase Auth's "letters_digits". */
export const isPassword = (password: string): boolean =>
  password.length >= 8 && password.length <= 64 && /[A-Za-z]/.test(password) && /[0-9]/.test(password);

/** Up to 100 characters, at most 64 before the @, and something.something after it. */
export const isEmail = (email: string): boolean =>
  email.length <= 100 && /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email) && email.indexOf('@') <= 64;
