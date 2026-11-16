// Phase 10, D5: the staff Edge Functions (create-user, reset-password) check the same password and email rules as the app
// (AccountRules in Services.cs), so a modified app can't make a weak temporary password. Node loads the .ts file as is.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { isEmail, isPassword } from '../../functions/_shared/rules.ts';

let passed = 0, failed = 0;
function test(name, fn) {
  try { fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}

test('a password needs 8 to 64 characters with a letter and a number (a to z, 0 to 9, as Supabase Auth counts them)', () => {
  for (const bad of ['', 'abcde12', 'abcdefgh', '12345678', 'ññññññ12', 'абвгдеж1', 'x'.repeat(64), 'x'.repeat(64) + '1'])
    assert.equal(isPassword(bad), false, bad);
  for (const good of ['abcdefg1', 'temp1234', 'x'.repeat(63) + '1', 'Pässwört9']) assert.equal(isPassword(good), true, good);
});
test('an email is up to 100 characters, 64 before the @, with a dot after it', () => {
  for (const bad of ['nobody', 'a@b', 'a b@c.com', 'a@@c.com', 'a'.repeat(65) + '@demo.io', 'a@' + 'b'.repeat(95) + '.com'])
    assert.equal(isEmail(bad), false, bad);
  for (const good of ['a@demo.io', 'a'.repeat(64) + '@demo.io', 'a@' + 'b'.repeat(94) + '.com']) assert.equal(isEmail(good), true, good);
});
test('both staff functions use the shared rules, so neither falls back to a length check', () => {
  for (const fn of ['create-user', 'reset-password']) {
    const src = fs.readFileSync(new URL(`../../functions/${fn}/index.ts`, import.meta.url), 'utf8');
    assert.match(src, /from '\.\.\/_shared\/rules\.ts'/, fn);
    assert.match(src, /!isPassword\(password\)\) return reply\(\{ error: 'bad_password' \}/, fn);
    assert.doesNotMatch(src, /password\.length/, fn);
  }
});

// Xendit plan (2026-11-16): xendit-webhook believes only the verification token; an unset secret must refuse everything
const { sameToken } = await import('../../functions/_shared/xendit.ts');
for (const [name, given, expected, want] of [
  ['the right token is accepted', 'tok-123', 'tok-123', true],
  ['a wrong token is refused', 'tok-124', 'tok-123', false],
  ['a prefix of the token is refused', 'tok-12', 'tok-123', false],
  ['no token is refused', '', 'tok-123', false],
  ['an unset secret refuses even an empty token', '', '', false],
]) {
  try { assert.equal(await sameToken(given, expected), want); passed++; console.log('  ok  ', `webhook token: ${name}`); }
  catch (e) { failed++; console.log('  FAIL', `webhook token: ${name}`, e.message); }
}

// final review: replace a stored session only once Xendit says it can't be paid; end sessions 5 minutes before the payment's deadline
const { nextStep, sessionExpiry } = await import('../../functions/_shared/xendit.ts');
const min = 60_000, now = Date.parse('2026-11-16T08:00:00Z');
for (const [name, actual, want] of [
  ['no session yet: make one', nextStep(null), 'create'],
  ['an open session is reused', nextStep('ACTIVE'), 'reuse'],
  ['a paid session is confirmed', nextStep('COMPLETED'), 'confirm'],
  ['an expired session is replaced', nextStep('EXPIRED'), 'create'],
  ['a cancelled session is replaced', nextStep('CANCELED'), 'create'],
  ['any other state is left alone', nextStep('PENDING'), 'wait'],
  ['a far deadline: 30 minutes', sessionExpiry(now + 48 * 60 * min, now), now + 30 * min],
  ['a near deadline: 5 minutes before it', sessionExpiry(now + 20 * min, now), now + 15 * min],
  ['under 5 minutes plus 1 left: too late to start', sessionExpiry(now + 5 * min + 30_000, now), null],
]) {
  try { assert.deepEqual(actual, want); passed++; console.log('  ok  ', `xendit session: ${name}`); }
  catch (e) { failed++; console.log('  FAIL', `xendit session: ${name}`, e.message); }
}

console.log(`Edge rules: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
