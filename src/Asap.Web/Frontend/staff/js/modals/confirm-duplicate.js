import { showConfirm } from '../dialogs.js';
import { closeDuplicateRequest } from '../actions.js';

export async function confirmDuplicateOpenRequestClose(err, identity, isCurrent = () => true) {
  const confirmed = await showConfirm(
    'Duplicate request found',
    'This patron already has an open request or hold for this BIB ID. Close this request as a duplicate, or keep editing and choose another BIB.'
  );
  return confirmed && isCurrent()
    ? closeDuplicateRequest(identity, { alreadyConfirmed: true, isCurrent, refresh: false })
    : false;
}
