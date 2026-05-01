export function runLegacyModule(env, source) {
  // The former staff app relied on classic script globals. This compatibility
  // runner keeps that lookup behavior while the code is split into ES modules.
  new Function('env', 'with (env) {\n' + source + '\n}')(env);
}
