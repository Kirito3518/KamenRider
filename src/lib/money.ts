const baht = new Intl.NumberFormat("th-TH", {
  style: "currency", currency: "THB", minimumFractionDigits: 2, maximumFractionDigits: 2,
});

export function formatBalance(balanceSatang: number) {
  return baht.format(balanceSatang / 100);
}
