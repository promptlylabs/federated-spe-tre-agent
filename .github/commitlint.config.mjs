import { RuleConfigSeverity } from '@commitlint/types';

export default {
  extends: ['@commitlint/config-conventional'],
  parserPreset: 'conventional-changelog-conventionalcommits',
  rules: {
    'scope-enum': [RuleConfigSeverity.Error, 'always', [
        '',
        'deps',
        'helm',
        'ui',
        'api',
        'federated',
        'egress'
    ]],
    'subject-case': [RuleConfigSeverity.Error, 'never', []],
  }
};