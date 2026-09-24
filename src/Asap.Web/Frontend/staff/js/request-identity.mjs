export const TITLE_REQUEST_TYPE = 'title_request';
export const ADDITIONAL_COPY_TYPE = 'additional_copy';
export let editRequestGeneration = 0;

export function requestType(value) {
  return value === ADDITIONAL_COPY_TYPE ? ADDITIONAL_COPY_TYPE : TITLE_REQUEST_TYPE;
}

export function requestIdentity(value, id) {
  const source = value && typeof value === 'object' ? value : { type: value, id };
  return {
    type: requestType(source.type),
    id: String(source.id || '').trim()
  };
}

export function requestIdentityKey(value, id) {
  const identity = requestIdentity(value, id);
  return JSON.stringify([identity.type, identity.id]);
}

export function requestIdentityDomKey(value, id) {
  const identity = requestIdentity(value, id);
  return `${identity.type}-${identity.id}`;
}

export function sameRequestIdentity(left, right) {
  const leftIdentity = requestIdentity(left);
  const rightIdentity = requestIdentity(right);
  return leftIdentity.type === rightIdentity.type && leftIdentity.id === rightIdentity.id;
}

export function findWorkflowRow(identity, ...collections) {
  const target = requestIdentity(identity);
  if (!target.id) return undefined;
  for (const collection of collections) {
    const row = (collection || []).find(item => sameRequestIdentity(item, target));
    if (row) return row;
  }
  return undefined;
}

export function requestIdentityFromElement(element, idAttribute = 'data-suggestion-id') {
  return requestIdentity({
    type: element?.getAttribute('data-request-type'),
    id: element?.getAttribute(idAttribute)
  });
}

export function setEditRequestIdentity(input, identity) {
  const value = requestIdentity(identity);
  input.value = value.id;
  input.dataset.requestType = value.type;
  delete input.dataset.requestVersion;
  editRequestGeneration += 1;
  return value;
}

export function editRequestIdentity(input) {
  return requestIdentity({ type: input?.dataset.requestType, id: input?.value });
}
