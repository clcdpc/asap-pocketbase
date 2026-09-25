// Display text retained from the pinned staff form. These are presentation
// fallbacks only; the server decides whether an edited value is saved.
export const patronFormDefaults = Object.freeze({
  pageTitle: '',
  barcodeLabel: '',
  pinLabel: '',
  loginPrompt: 'Please enter your information below to start the suggestion process.',
  loginNote: 'Use of this service requires a valid library card. Contact your library if you need assistance with your card or PIN.',
  suggestionFormNote: 'If the library approves your suggestion for purchase, we will email you while it is awaiting ordering and cataloging. Once the item is available in the catalog, we will automatically place a hold when possible and send another update.',
  noEmailMessage: 'No email is specified on your library account, which means we won\'t be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.',
  successTitle: 'Suggestion Submitted',
  successMessage: 'You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>',
  alreadySubmittedMessage: 'This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library\'s suggestion service.</div>',
  ebookMessage: '',
  eaudiobookMessage: ''
});

export const systemMessageFormDefaults = Object.freeze({
  systemNotEnabledMessage: '{{library}} does not currently participate in this suggestion service.',
  misconfiguredMessage: 'The {{library}} suggestion system is currently misconfigured. Please contact staff.'
});
