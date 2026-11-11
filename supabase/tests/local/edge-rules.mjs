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

console.log(`Edge rules: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
